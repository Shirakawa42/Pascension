using System;
using System.Linq;
using System.Threading;

namespace Shards.ZeroDepth
{
    // One control thread publishes disjoint lane jobs to a persistent team.
    // Returning from Run means every lane write is complete and visible.
    internal sealed class LaneWorkers : IDisposable
    {
        internal readonly int Workers;
        private readonly Thread[] _threads;
        private readonly AutoResetEvent[] _signals;
        private readonly CountdownEvent _finished;
        private Action<int> _action;
        private int _next, _count;
        private bool _disposed;
        private volatile bool _stop;
        private Exception _error;

        internal LaneWorkers(int workers)
        {
            if(workers<1||workers>128)throw new ArgumentOutOfRangeException(nameof(workers));
            Workers=workers;_threads=new Thread[workers-1];_signals=new AutoResetEvent[workers-1];
            _finished=new CountdownEvent(workers-1);
            for(int i=0;i<_threads.Length;i++)
            {
                int seat=i;_signals[i]=new AutoResetEvent(false);
                _threads[i]=new Thread(()=>Loop(seat)){IsBackground=true,Name="Shards lane "+(i+1)};
                _threads[i].Start();
            }
        }
        internal void Run(int count,Action<int> action)
        {
            if(_disposed)throw new ObjectDisposedException(nameof(LaneWorkers));
            if(count<0)throw new ArgumentOutOfRangeException(nameof(count));
            if(action==null)throw new ArgumentNullException(nameof(action));
            // Evaluation jobs include expensive incumbent search; distribute them individually.
            if(Workers==1||count<=1)
            {
                try{for(int i=0;i<count;i++)action(i);}
                catch(Exception error){if(Workers>1)throw new AggregateException(error);throw;}
                return;
            }
            _count=count;_action=action;_next=0;_error=null;_finished.Reset(Workers-1);
            foreach(var signal in _signals)signal.Set();
            Execute();_finished.Wait();_action=null;
            if(_error!=null)throw new AggregateException(_error);
        }
        private void Loop(int seat)
        {
            while(true)
            {
                _signals[seat].WaitOne();if(_stop)return;
                Execute();_finished.Signal();
            }
        }
        private void Execute()
        {
            try
            {
                while(true)
                {
                    int first=Interlocked.Increment(ref _next)-1;if(first>=_count)return;
                    for(int i=first;i<Math.Min(first+1,_count);i++)_action(i);
                }
            }
            catch(Exception error){Interlocked.CompareExchange(ref _error,error,null);}
        }
        public void Dispose()
        {
            if(_disposed)return;_disposed=true;_stop=true;
            foreach(var signal in _signals)signal.Set();
            foreach(var thread in _threads)thread.Join();
            foreach(var signal in _signals)signal.Dispose();_finished.Dispose();
        }
        internal static object SelfTest()
        {
            int batches=0,errors=0;
            for(int repeat=0;repeat<20;repeat++)foreach(int workers in new[]{1,2,8,16})
            {
                using var team=new LaneWorkers(workers);
                foreach(int count in new[]{0,1,2,3,7,8,9,127,128,513})
                {
                    var visits=new int[count];var results=new int[count];
                    team.Run(count,i=>{Interlocked.Increment(ref visits[i]);results[i]=i*i+17;});
                    if(visits.Any(v=>v!=1)||results.Where((v,i)=>v!=i*i+17).Any())
                        throw new InvalidOperationException("Persistent lane worker lost, repeated or published unfinished work");
                    batches++;
                }
            }
            foreach(int workers in new[]{2,8,16})
            {
                using var team=new LaneWorkers(workers);bool caught=false;
                try{team.Run(128,i=>{if(i==7)throw new InvalidOperationException("worker sentinel");});}
                catch(AggregateException error){caught=error.InnerException is InvalidOperationException&&error.InnerException.Message=="worker sentinel";}
                if(!caught)throw new InvalidOperationException("Persistent lane worker swallowed or changed a callback failure");
                var visits=new int[128];team.Run(128,i=>Interlocked.Increment(ref visits[i]));
                if(visits.Any(v=>v!=1))throw new InvalidOperationException("Callback failure corrupted the following lane batch");errors++;
            }
            var closed=new LaneWorkers(8);closed.Dispose();closed.Dispose();bool rejected=false;
            try{closed.Run(1,_=>{});}catch(ObjectDisposedException){rejected=true;}
            if(!rejected)throw new InvalidOperationException("Disposed lane workers accepted a new batch");
            using var replacement=new LaneWorkers(8);var after=new int[128];replacement.Run(128,i=>after[i]=i+1);
            if(after.Where((v,i)=>v!=i+1).Any())throw new InvalidOperationException("Replacement lane worker team failed");
            return new{passed=true,batches,worker_exception_cases=errors,idempotent_dispose=true,disposed_team_rejected=true,replacement_team_ok=true};
        }
    }
}
