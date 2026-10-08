using System;
using System.Collections.Generic;
using Pascension.Engine.Actions;
using Pascension.Engine.Decisions;
using Shards.Engine;

namespace Shards.ZeroDepth
{
    // Training-only initial conditions. This never changes rules or consumes
    // the game's PCG stream, and always assigns heroes through real draft Submit.
    internal static class HeroAssignments
    {
        internal const string Version="shards-balanced-heroes-v1-splitmix64";
        internal const string EnvironmentKey="SHARDS_ZERO_HERO_MODE";
        internal const string Policy="policy",BalancedRandom="balanced_random";
        internal const int MatchupsPerCycle=20;

        internal static string ParseMode(string mode)
        {
            if(mode==null||mode==Policy)return Policy;
            if(mode==BalancedRandom)return BalancedRandom;
            throw new ArgumentException("Hero mode must be policy or balanced_random");
        }
        private static ulong Next(ref ulong state)
        {
            state=unchecked(state+0x9e3779b97f4a7c15UL);
            ulong value=state;
            value=unchecked((value^(value>>30))*0xbf58476d1ce4e5b9UL);
            value=unchecked((value^(value>>27))*0x94d049bb133111ebUL);
            return value^(value>>31);
        }
        private static int Bounded(ref ulong state,int bound)
        {
            ulong limit=(ulong)bound,threshold=unchecked(0UL-limit)%limit,value;
            do value=Next(ref state);while(value<threshold);
            return (int)(value%limit);
        }
        internal static (string Seat0,string Seat1,int Matchup) ForSeed(ulong gameSeed)
        {
            var heroes=ShardsEngine.DraftableCharacters;
            if(heroes.Length!=5)throw new InvalidOperationException("Review balanced hero sampler when the five-hero catalog changes");
            Span<int> permutation=stackalloc int[MatchupsPerCycle];
            for(int i=0;i<permutation.Length;i++)permutation[i]=i;
            ulong state=gameSeed/MatchupsPerCycle^0xd1b54a32d192ed03UL;
            for(int i=permutation.Length-1;i>0;i--)
            {int chosen=Bounded(ref state,i+1);int value=permutation[i];permutation[i]=permutation[chosen];permutation[chosen]=value;}
            int matchup=permutation[(int)(gameSeed%MatchupsPerCycle)],first=matchup/4,second=matchup%4;
            if(second>=first)second++;
            return (heroes[first],heroes[second],matchup);
        }
        internal static void Apply(Adapter game,ulong gameSeed)
        {
            if((game.Engine.State.Dlc&ShardsDlc.Duel)==0||game.Decision?.Context!="soi.herodraft")
                throw new InvalidOperationException("Balanced heroes require the untouched initial Duel draft");
            var pair=ForSeed(gameSeed);int submitted=0;
            while(game.Decision?.Context=="soi.herodraft")
            {
                if(submitted>=2)throw new InvalidOperationException("Balanced initial draft cannot exceed two submissions");
                var request=game.Decision;
                string hero=request.PlayerIndex==0?pair.Seat0:request.PlayerIndex==1?pair.Seat1:
                    throw new InvalidOperationException("Balanced hero host supports two seats");
                var option=request.Options.Find(o=>o.DefId==hero&&!o.Disabled);
                if(option==null)throw new InvalidOperationException("Assigned hero is unavailable in actual draft");
                game.ApplyExternal(new SubmitDecisionAction{PlayerIndex=request.PlayerIndex,
                    Answer=new DecisionAnswer{DecisionId=request.Id,ChosenOptionIds=new List<int>{option.Id}}});
                submitted++;
            }
            if(submitted!=2||game.Engine.State.Players[0].CharacterId!=pair.Seat0||game.Engine.State.Players[1].CharacterId!=pair.Seat1)
                throw new InvalidOperationException("Actual draft did not produce the assigned matchup");
        }
    }
}
