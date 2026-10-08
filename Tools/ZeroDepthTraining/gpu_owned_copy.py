"""Copy an actor's complete raw prefix into owned float32 rollout storage.

One optional Triton launch replaces four independent copies. Unsupported
layouts return False so the caller keeps its ordinary Torch copy path.
Only device-owned tensors reach this kernel; host/SHM pointers never do.
"""
import torch
try:
    import triton
    import triton.language as tl
except ImportError:
    triton = None

if triton is not None:
    @triton.jit
    def _copy_prefix(SObs,SCandidates,SMask,SPacket,DObs,DCandidates,DMask,DPacket,
                     First, Obs:tl.constexpr, Candidates:tl.constexpr,
                     Actions:tl.constexpr, Block:tl.constexpr):
        row=tl.program_id(0)
        column=tl.program_id(1)*Block+tl.arange(0,Block)
        obs=tl.load(SObs+row*Obs+column,column<Obs,other=0)
        tl.store(DObs+(row+First)*Obs+column,obs,column<Obs)
        cc=column-Obs
        candidate=tl.load(SCandidates+row*Candidates+cc,(cc>=0)&(cc<Candidates),other=0)
        tl.store(DCandidates+(row+First)*Candidates+cc,candidate,(cc>=0)&(cc<Candidates))
        mc=cc-Candidates
        mask=tl.load(SMask+row*Actions+mc,(mc>=0)&(mc<Actions),other=0)
        tl.store(DMask+(row+First)*Actions+mc,mask!=0,(mc>=0)&(mc<Actions))
        pc=mc-Actions
        packet=tl.load(SPacket+row*3+pc,(pc>=0)&(pc<3),other=0)
        tl.store(DPacket+(row+First)*3+pc,packet,(pc>=0)&(pc<3))


def try_copy_prefix(store, actor, count):
    """Copy the canonical Actor/Rollout buffers when their layout is supported.

    This internal fast path requires the independently allocated fields supplied
    by Actor and Rollout: destination fields cannot overlap one another or any
    source field. Arbitrary aliased tensor views are outside this interface.
    """
    if triton is None:
        return False
    if type(count) is not int or count < 0:
        return False
    if count == 0:
        return True
    fields = ("obs", "candidates", "mask", "packet")
    sources = tuple(getattr(actor, name) for name in fields)
    destinations = tuple(getattr(store, name) for name in fields)
    first = store.rows
    if type(first) is not int or first < 0 or first + count > store.capacity:
        return False
    device = destinations[0].device
    if device.type != "cuda":
        return False
    for name, source, destination in zip(fields, sources, destinations):
        if (source.device != device or destination.device != device or
                source.dtype != torch.float32 or
                destination.dtype != (torch.bool if name == "mask" else torch.float32) or
                not source.is_contiguous() or not destination.is_contiguous() or
                source.ndim < 1 or source.ndim != destination.ndim or source.shape[1:] != destination.shape[1:] or
                source.shape[0] < count or destination.shape[0] < first + count):
            return False
    obs, candidates, mask, packet = destinations
    if (obs.ndim != 2 or candidates.ndim != 3 or mask.ndim != 2 or packet.ndim != 2 or
            packet.shape[1] != 3 or candidates.shape[1] != mask.shape[1]):
        return False
    observation_width = obs.shape[1]
    candidate_width = candidates.shape[1] * candidates.shape[2]
    actions = mask.shape[1]
    # The proven kernel uses int32 row/column arithmetic. Large valid owned
    # offsets retain Torch's ordinary copies, including full-capacity cohorts.
    limit = (1 << 31) - 1
    if any(max(count * width, (first + count) * width) > limit
           for width in (observation_width, candidate_width, actions, 3)):
        return False
    blocks = triton.cdiv(observation_width+candidate_width+actions+3,2048)
    if blocks * 2048 - 1 > limit:
        return False
    _copy_prefix[(count, blocks)](
        *sources, *destinations, first, observation_width, candidate_width, actions, 2048)
    return True
