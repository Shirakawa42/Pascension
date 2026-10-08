"""Lossless sparse storage of real searched decisions, never hypothetical leaves."""
import numpy as np


class SearchExperience:
    def __init__(self,width):
        if not 1<=width<=65536:raise ValueError('Sparse column format requires width <= 65536')
        self.width=width;self.columns=[];self.values=[];self.counts=[];self.meta=[];self.actions=[];self.improved=[]

    def append(self,packet,meta,actions,improved):
        if packet.shape!=(len(meta),self.width) or not np.isfinite(packet).all():raise ValueError('Invalid real root packet')
        rows,columns=np.nonzero(packet)
        self.columns.append(columns.astype(np.uint16));self.values.append(packet[rows,columns].astype(np.float32))
        self.counts.append(np.bincount(rows,minlength=len(packet)).astype(np.uint32))
        self.meta.append(np.array(meta,dtype=np.int32,copy=True));self.actions.append(np.array(actions,dtype=np.int16,copy=True))
        self.improved.append(np.array(improved,dtype=np.uint8,copy=True))

    def arrays(self,games):
        if not self.meta:raise ValueError('No actual decisions recorded')
        count=np.concatenate(self.counts);meta=np.concatenate(self.meta);lookup={g['lane']:g for g in games}
        if not all(g['completed'] for g in games):raise ValueError('Do not label censored trajectories')
        outcomes=np.array([0 if lookup[lane]['winner']<0 else 1 if lookup[lane]['winner']==seat else -1 for lane,seat in meta],np.float32)
        return dict(columns=np.concatenate(self.columns),values=np.concatenate(self.values),
            offsets=np.r_[np.uint64(0),np.cumsum(count,dtype=np.uint64)],width=np.int32(self.width),
            meta=meta,actions=np.concatenate(self.actions),improved=np.concatenate(self.improved),outcomes=outcomes,
            seeds=np.array([lookup[lane]['seed'] for lane,_ in meta],np.uint64))


def dense_rows(data,indices):
    indices=np.asarray(indices,dtype=np.int64);result=np.zeros((len(indices),int(data['width'])),np.float32)
    # NpzFile fields decompress on every access; materialize each array once
    # per call rather than decompressing a whole cohort for every selected row.
    offsets=data['offsets'];columns=data['columns'];values=data['values']
    for row,index in enumerate(indices):
        start,end=offsets[index:index+2];result[row,columns[start:end]]=values[start:end]
    return result
