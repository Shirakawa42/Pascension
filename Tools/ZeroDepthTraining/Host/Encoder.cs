using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Pascension.Engine.Decisions;
using Pascension.Engine.Targeting;
using Shards.Engine;

namespace Shards.ZeroDepth
{
    internal static class Encoder
    {
        internal const int ObsDim = 24576, MaxActions = 64, ActionDim = 48, CardCapacity = 192;
        internal const string SchemaVersion = "shards-zero-depth-observation-v2";
        internal const int HistogramOffset = 256, EntityOffset = 4864, OptionOffset = 9472,
            KnowledgeOffset = 17152, TraceOffset = 20224, PlayedOffset = 21760, ContinuationOffset = 23296, CopyOffset = 23808;
        internal const int Capacity = 384;
        internal static string[] CardIds;
        private static ShardsCardDef[] Definitions;
        private static Dictionary<string,int> _codes;
        private static readonly ConcurrentDictionary<uint,string> TextIdentities = new();
        private static readonly ConcurrentDictionary<(string hero,ShardsDlc dlc),string[]> RelicCatalog = new();
        internal static readonly string[] Histograms = { "own_hand", "own_draw_composition", "own_discard", "own_play",
            "own_champions", "own_destinies", "own_set_aside", "opponent_permanent_collection", "opponent_discard",
            "opponent_play", "opponent_champions", "opponent_destinies", "opponent_inferred_set_aside", "market",
            "shared_destiny_row", "active_monsters", "banished", "opponent_known_hand", "initial_card_counts",
            "own_played_this_turn", "opponent_played_this_turn", "inferred_center_composition", "inferred_undealt_destinies", "own_permanent_collection" };
        private static readonly string[] Contexts = { "", "soi.banish", "soi.copy", "soi.destroy", "soi.keepfast", "soi.mode",
            "soi.removeshop", "soi.return", "soi.reveal", "soi.target", "soi.tutor", "soi.warp", "soi.confirm", "soi.defiant",
            "soi.destiny", "soi.relic", "soi.reset", "soi.reorder", "soi.scry", "soi.volos", "soi.discard", "soi.recruit",
            "soi.herodraft", "soi.maglev", "soi.shields", "soi.split" };
        private static readonly FieldInfo ActiveContext = Field("_activeContext"), Queue = Field("_effectQueue"),
            PendingHits = Field("_pendingChampionHits"), SplitTargets = Field("_splitTargets"), SplitAmounts = Field("_splitAmounts");
        private static FieldInfo Field(string name) => typeof(ShardsEngine).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Audited engine field changed: " + name);
        internal static void Initialize()
        {
            CardIds = ShardsCardDatabase.All.Select(c=>c.Id).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
            if (CardIds.Length > CardCapacity) throw new InvalidOperationException("Card catalog exceeds schema capacity");
            _codes = CardIds.Select((id,i)=>(id,i)).ToDictionary(p=>p.id,p=>p.i,StringComparer.Ordinal);
            Definitions=CardIds.Select(ShardsCardDatabase.Get).ToArray();
            RelicCatalog.Clear();
            Shards.AI.Encoder.Initialize();
        }
        internal static int CardIndex(string id) => id != null && _codes.TryGetValue(id,out int code) ? code : -1;
        internal static string[] CatalogRelics(string hero,ShardsDlc dlc)=>RelicCatalog.GetOrAdd((hero,dlc),static key=>ShardsEngine.RelicIdsFor(key.hero,key.dlc).ToArray());
        internal static float CardCode(string id)
        {
            int code=CardIndex(id);
            if (id != null && code<0 && Shards.AI.Encoder.HeroIndex(id)<0 && !id.StartsWith(VolosAbilityChoice.FacePrefix,StringComparison.Ordinal))
                throw new InvalidOperationException("Unknown visible card definition: " + id);
            return (code+1)/192f;
        }
        internal static object Catalog()
        {
            var effects=Shards.Preflight.EffectDescriptors.AuditMatrix();
            return new { obs_dim=ObsDim,max_actions=MaxActions,action_dim=ActionDim,card_ids=CardIds,
                observation_schema=SchemaVersion,candidate_card_column=16,candidate_card_scale=192,card_code_normalization=192,
                histogram_descriptors=Histograms.Select((name,i)=>new{name,offset=HistogramOffset+i*CardCapacity,length=CardIds.Length,scale=10}).ToArray(),
                card_features=effects.Skip(1).Take(CardIds.Length).ToArray(),card_feature_descriptor=Shards.Preflight.EffectDescriptors.Descriptor,
                static_catalog=CardIds.Select(id=> {var d=ShardsCardDatabase.Get(id);return new {id,d.Name,d.Set,d.Faction,d.Type,d.Cost,d.Quantity,d.Defense,d.Shield,d.RulesText,
                    d.ExhaustGemCost,d.Taunt,d.CountsAsEveryFaction,d.CannotBeRerolled,d.CannotBeFastPlayed,d.ShieldsProtectChampions,d.ShieldInPlay,
                    d.KeepFastPlaysAtMastery,d.ImmuneToIngeminex,d.DoublesExhaustsAtMastery,d.Character,d.ReplacesId,
                    d.ReturnsFromDiscardOnChampionPlay,d.KeepFastPlaysCharacter,d.RedirectChampionRecruitsToDeckTop,d.RecruitsToHand,d.ReturnFromDiscardOnFactionPlay,
                    has_attack_veto=d.CanBeAttacked!=null,has_defense_aura=d.DefenseAura!=null,has_cost_modifier=d.CostModifier!=null,
                    has_damage_trigger=d.OnDamageDealt!=null,has_dynamic_shield=d.DynamicShield!=null,has_discard_shield=d.DiscardPassiveShield!=null};}).ToArray(),
                tables=new {public_entities=new{offset=EntityOffset,capacity=Capacity,stride=12,count_column=166,count_scale=384},all_pending_options=new{offset=OptionOffset,capacity=Capacity,stride=20,count_column=149,count_scale=384},
                    known_positions=new{offset=KnowledgeOffset,capacity=Capacity,stride=8,count_column=167,count_scale=384,public_identity_column=7,public_identity_scale=65536,known_hand_kind=4,unknown_hand_position=-1},staged_selection=new{offset=TraceOffset,capacity=Capacity,stride=4,count_column=177,count_scale=384},
                    played_this_turn=new{offset=PlayedOffset,capacity=Capacity,stride=4,count_column=168,count_scale=384},public_continuations=new{offset=ContinuationOffset,capacity=64,stride=8,count_column=174,count_scale=64},
                    public_copy_frames=new{offset=CopyOffset,capacity=96,stride=8,count_column=176,count_scale=96}},
                public_reference_encoding="positive: public-entity ordinal/384; negative: certainly-present known-position ordinal/384; zero: unavailable",
                contexts=Contexts,overflow_policy="fail; never truncate",normalization="fixed division without clipping",
                heroes=ShardsEngine.DraftableCharacters.Select(id=>{var a=ShardsEngine.HeroAbilityInfo(id);return new{id,ability=new{name=a.Name,text=a.Text,gems=a.Gems,health=a.Health},relics=ShardsEngine.RelicIdsFor(id,ShardsDlc.Duel|ShardsDlc.RelicsOfTheFuture|ShardsDlc.ShadowOfSalvation|ShardsDlc.IntoTheHorizon)};}).ToArray(),
                field_inventory=new[]{"all public ShardsPlayer resource/turn/lifetime fields and faction counters", "own hand and own unordered draw composition",
                    "both permanent collection multisets; every separate public zone and banished owner", "canonical entities linked to all pending options and all legal candidates",
                    "every pending option including disabled and off-page; target kind/owner/amount/default/selected/source ordinal", "full ordered staged answers and PlayedThisTurn",
                    "remembered public enemy hand; uncapped remembered personal and private-seat center top/bottom position facts", "all rules/DLC/initial counts; inferred remaining center/destiny pool composition",
                    "pending monster order/flags, deferred champion allocations and face assignment, reviewed public effect source/queue order",
                    "public chosen copy/mode identities, recursion guards, nested active/pending replay effects and remaining repetitions"},
                limitations=new[]{"Historical public actions are summarized into current visible state and remembered card facts, not replayed as an unbounded event-history tensor.",
                    "After an unobserved private reorder/removal, position intervals retain remembered cards; correlations between those intervals are not a full Bayesian belief state.",
                    "Text identities use four exact hash bytes with collision detection and numeric substrings; static card text/effect descriptors are catalog metadata, not language-model token embeddings.",
                    "Bespoke iterator continuation locals are not introspected; public sources/queues, chosen copy/mode identities and annotated copy/replay scopes are represented."} };
        }
        private static void Accumulate(List<ShardsCard> cards,Span<int> counts,Span<int> populated,ref int used,bool permanent=false)
        {
            for(int i=0;i<cards.Count;i++)
            {
                var c=cards[i];if(c==null||(permanent&&c.FastPlayed))continue;
                int code=CardIndex(c.DefId);if(counts[code]++==0)populated[used++]=code;
            }
        }
        private static void WriteCounts(Span<int> counts,Span<int> populated,int used,Span<float> obs,int channel)
        {
            // Encode already cleared every histogram. Preserve integer counts and
            // the exact original division while avoiding writes of empty bins.
            for(int i=0;i<used;i++){int code=populated[i];obs[HistogramOffset+channel*CardCapacity+code]=counts[code]/10f;}
        }
        private static void Count(List<ShardsCard> cards,Span<float> obs,int channel)
        {
            Span<int> counts=stackalloc int[CardCapacity];counts.Clear();
            Span<int> populated=stackalloc int[CardCapacity];int used=0;
            Accumulate(cards,counts,populated,ref used);
            WriteCounts(counts,populated,used,obs,channel);
        }
        private static void Count(ShardsCard[] cards,Span<float> obs,int channel)
        {
            Span<int> counts=stackalloc int[CardCapacity];counts.Clear();
            Span<int> populated=stackalloc int[CardCapacity];int used=0;
            for(int i=0;i<cards.Length;i++)
            {
                var c=cards[i];if(c==null)continue;
                int code=CardIndex(c.DefId);if(counts[code]++==0)populated[used++]=code;
            }
            WriteCounts(counts,populated,used,obs,channel);
        }
        private static void CountCollection(ShardsPlayer p,Span<float> obs,int channel)
        {
            Span<int> counts=stackalloc int[CardCapacity];counts.Clear();
            Span<int> populated=stackalloc int[CardCapacity];int used=0;
            Accumulate(p.Deck,counts,populated,ref used);Accumulate(p.Hand,counts,populated,ref used);
            Accumulate(p.Discard,counts,populated,ref used);Accumulate(p.PlayZone,counts,populated,ref used,permanent:true);
            Accumulate(p.Champions,counts,populated,ref used);
            WriteCounts(counts,populated,used,obs,channel);
        }
        internal static List<ShardsCard> VisibleEntities(Adapter g)
        {
            var s=g.Engine.State;int actor=g.Actor;
            var result=g.EntityBuffer;result.Clear();
            g.SourceOrdinals.Clear();
            AddEntities(g,s.Players[actor].Hand);AddEntities(g,s.Players[actor].SetAside);
            foreach(var p in s.Players) { AddEntities(g,p.Discard);AddEntities(g,p.PlayZone);AddEntities(g,p.Champions);AddEntities(g,p.Destinies); }
            for(int i=0;i<s.CenterRow.Length;i++)if(s.CenterRow[i]!=null){result.Add(s.CenterRow[i]);g.SourceOrdinals.Add(s.CenterRow[i].InstanceId,i+1);}
            AddEntities(g,s.DestinyRow);AddEntities(g,s.ActiveMonsters);AddEntities(g,s.Banished);
            // A public reveal ledger records the SAME physical instance. Once
            // that instance is visible in an ordinary zone, prefer its current
            // authoritative status and retire only that stale ledger identity.
            for(int i=g.Knowledge.Detached.Count-1;i>=0;i--)
                if(g.SourceOrdinals.ContainsKey(g.Knowledge.Detached[i].InstanceId))
                {
                    var fact=g.Knowledge.Detached[i];var current=result.Find(c=>c.InstanceId==fact.InstanceId);
                    if(current.DefId!=fact.DefId)throw new InvalidOperationException("Public instance identity changed definition");
                    g.Knowledge.Detached.RemoveAt(i);
                }
            AddEntities(g,g.Knowledge.Detached);
            // Revealed options can live in an iterator-owned temporary list. Their
            // visible DefId is encoded in the option table, not found through hidden zones.
            result.Sort((a,b)=> {int c=Relative(a.Owner,actor).CompareTo(Relative(b.Owner,actor));if(c!=0)return c;
                c=((int)a.Zone).CompareTo((int)b.Zone);if(c!=0)return c;c=string.CompareOrdinal(a.DefId,b.DefId);if(c!=0)return c;
                c=a.Exhausted.CompareTo(b.Exhausted);if(c!=0)return c;c=a.DamageThisTurn.CompareTo(b.DamageThisTurn);if(c!=0)return c;
                c=a.FastPlayed.CompareTo(b.FastPlayed);if(c!=0)return c;c=a.BanishAtCleanup.CompareTo(b.BanishAtCleanup);if(c!=0)return c;return a.InstanceId.CompareTo(b.InstanceId);});
            if(result.Count>Capacity) throw new InvalidOperationException("Public entity capacity exceeded: "+result.Count);
            return result;
        }
        private static void AddEntities(Adapter g,List<ShardsCard> cards)
        {
            for(int i=0;i<cards.Count;i++)
            {
                if(g.SourceOrdinals.ContainsKey(cards[i].InstanceId))
                    throw new InvalidOperationException($"Physical card occurs twice in authoritative visible zones: {cards[i].InstanceId} {cards[i].DefId} owner={cards[i].Owner} zone={cards[i].Zone}");
                g.EntityBuffer.Add(cards[i]);g.SourceOrdinals.Add(cards[i].InstanceId,i+1);
            }
        }
        internal static int Relative(int owner,int actor)=>owner<0?-1:owner==actor?0:1;
        internal static unsafe void Encode(Adapter g,Span<float> obs,Span<float> candidates,Span<float> mask)
        {
            fixed(float* pointer=obs)
            {
                if(g.ObservationAddress!=(IntPtr)pointer)
                {
                    obs.Clear();g.ObservationAddress=(IntPtr)pointer;
                    g.EncodedEntities=g.EncodedOptions=g.EncodedKnowledge=g.EncodedTrace=g.EncodedPlayed=g.EncodedQueue=g.EncodedCopy=0;
                }
                else
                {
                    obs.Slice(0,EntityOffset).Clear();
                    obs.Slice(EntityOffset,g.EncodedEntities*12).Clear();obs.Slice(OptionOffset,g.EncodedOptions*20).Clear();
                    obs.Slice(KnowledgeOffset,g.EncodedKnowledge*8).Clear();obs.Slice(TraceOffset,g.EncodedTrace*4).Clear();
                    obs.Slice(PlayedOffset,g.EncodedPlayed*4).Clear();obs.Slice(ContinuationOffset,g.EncodedQueue*8).Clear();
                    obs.Slice(CopyOffset,g.EncodedCopy*8).Clear();
                }
            }
            candidates.Clear();mask.Clear();
            var s=g.Engine.State;int actor=g.Actor;var own=s.Players[actor];var enemy=s.Players[1-actor];var d=g.Decision;
            obs[0]=actor;obs[1]=Relative(s.TurnPlayerIndex,actor);obs[2]=s.Round/100f;obs[3]=(int)s.Dlc/15f;
            obs[4]=s.CenterDeck.Count/256f;obs[5]=s.DestinyDeck.Count/64f;obs[6]=s.GameOver?1:0;obs[7]=Relative(s.WinnerIndex,actor);
            obs[8]=s.Rules.StartingHealth/50f;obs[9]=s.Rules.MaxHealth/50f;obs[10]=s.Rules.HandSize/10f;obs[11]=s.Rules.MasteryCap/30f;
            obs[12]=s.Rules.CenterRowSize/6f;obs[13]=s.Rules.ResponseTimerSeconds/60f;obs[14]=Relative(s.ExtraTurnForPlayer,actor);
            obs[15]=g.Engine.MonsterAttackActive?1:0;
            for(int relative=0;relative<2;relative++) Player(s.Players[relative==0?actor:1-actor],obs.Slice(16+relative*64,64));
            obs[144]=d!=null?1:0;obs[145]=d==null?0:(int)d.Kind+1;obs[146]=d?.Min/1000f??0;obs[147]=d?.Max/1000f??0;
            obs[148]=d?.Ordered==true?1:0;obs[149]=d?.Options.Count/384f??0;obs[150]=g.Selected.Count/1000f;
            obs[151]=g.Candidates.Count/384f;obs[152]=g.Page/10f;obs[153]=g.SplitTarget/384f;obs[154]=g.Remaining/1000f;obs[155]=g.Low/1000f;obs[156]=g.High/1000f;
            if(d!=null)
            {
                int context=Array.IndexOf(Contexts,d.Context??"");if(context<0)throw new InvalidOperationException("Unreviewed decision context: "+d.Context);
                obs[157]=context/(float)Contexts.Length;Text(d.Title,obs.Slice(158,8));
            }
            var entities=VisibleEntities(g);
            Count(own.Hand,obs,0);Count(own.Deck,obs,1);Count(own.Discard,obs,2);Count(own.PlayZone,obs,3);Count(own.Champions,obs,4);
            Count(own.Destinies,obs,5);Count(own.SetAside,obs,6);CountCollection(enemy,obs,7);Count(enemy.Discard,obs,8);Count(enemy.PlayZone,obs,9);
            Count(enemy.Champions,obs,10);Count(enemy.Destinies,obs,11);
            // Hero/DLC relic membership is immutable catalog data. Current
            // recruited identities are still checked below on every observation.
            foreach(string id in CatalogRelics(enemy.CharacterId,s.Dlc))
                if(!g.Knowledge.RecruitedRelicDefs[enemy.Index].Contains(id))obs[HistogramOffset+12*CardCapacity+CardIndex(id)]+=0.1f;
            Count(s.CenterRow,obs,13);Count(s.DestinyRow,obs,14);Count(s.ActiveMonsters,obs,15);Count(s.Banished,obs,16);
            foreach(string id in g.Knowledge.Hand[enemy.Index])obs[HistogramOffset+17*CardCapacity+CardIndex(id)]+=0.1f;
            var initial=g.InitialCounts();for(int i=0;i<CardIds.Length;i++)obs[HistogramOffset+18*CardCapacity+i]=initial[i]/10f;
            Count(own.PlayedThisTurn,obs,19);Count(enemy.PlayedThisTurn,obs,20);CountCollection(own,obs,23);
            foreach(var c in g.Knowledge.Detached)
            {
                int code=CardIndex(c.DefId);
                if(c.Owner==actor)obs[HistogramOffset+23*CardCapacity+code]+=0.1f;
                else if(c.Owner==enemy.Index)obs[HistogramOffset+7*CardCapacity+code]+=0.1f;
            }
            // Conservation-derived public composition: no center/destiny identities read.
            Span<int> loans=stackalloc int[CardCapacity];loans.Clear();
            foreach(var player in s.Players)foreach(var loan in player.PlayZone)if(loan.FastPlayed)loans[CardIndex(loan.DefId)]++;
            foreach(var c in g.Knowledge.Detached)if(c.Owner<0)loans[CardIndex(c.DefId)]++;
            int floods=(s.Players[0].DoomGateFloodUsed?1:0)+(s.Players[1].DoomGateFloodUsed?1:0);
            for(int card=0;card<CardIds.Length;card++)
            {
                var def=Definitions[card];float remaining=obs[HistogramOffset+18*CardCapacity+card];
                if(def.IsMonster) remaining+=floods*0.7f;
                if(def.Type==ShardsCardType.Destiny)
                    obs[HistogramOffset+22*CardCapacity+card]=remaining-obs[HistogramOffset+5*CardCapacity+card]-obs[HistogramOffset+11*CardCapacity+card]-obs[HistogramOffset+14*CardCapacity+card]-obs[HistogramOffset+16*CardCapacity+card];
                else if(def.Type!=ShardsCardType.Starter&&def.Type!=ShardsCardType.Relic)
                    obs[HistogramOffset+21*CardCapacity+card]=remaining-obs[HistogramOffset+23*CardCapacity+card]-obs[HistogramOffset+7*CardCapacity+card]-obs[HistogramOffset+13*CardCapacity+card]-obs[HistogramOffset+15*CardCapacity+card]-obs[HistogramOffset+16*CardCapacity+card]
                        -loans[card]/10f;
            }
            obs[166]=entities.Count/384f;
            g.EncodedEntities=entities.Count;
            var entityMap=g.EntityMap;entityMap.Clear();for(int i=0;i<entities.Count;i++)entityMap.Add(entities[i].InstanceId,i+1);
            var hits=PendingHits.GetValue(g.Engine) as Dictionary<int,List<(int hitId,int amount)>>;
            for(int i=0;i<entities.Count;i++)
            {
                var c=entities[i];var row=obs.Slice(EntityOffset+i*12,12);row[0]=CardCode(c.DefId);row[1]=Relative(c.Owner,actor);row[2]=g.Knowledge.Detached.Contains(c)?12/16f:((int)c.Zone+1)/16f;
                row[3]=c.Exhausted?1:0;row[4]=c.DamageThisTurn/50f;row[5]=Defense(g,c)/50f;row[6]=Shield(g,c)/20f;
                row[7]=c.FastPlayed?1:0;row[8]=c.BanishAtCleanup?1:0;
                row[9]=(s.PendingMonsterAttacks.IndexOf(c.InstanceId)+1)/384f;
                row[10]=g.SourceOrdinals[c.InstanceId]/384f;
                if(hits!=null)foreach(var owner in hits.Values)foreach(var hit in owner)if(hit.hitId==c.InstanceId)row[11]=hit.amount/1000f;
            }
            if(d!=null)
            {
                if(d.Options.Count>Capacity)throw new InvalidOperationException("Pending option capacity exceeded");
                for(int i=0;i<d.Options.Count;i++)Option(g,d.Options[i],i,entityMap,obs.Slice(OptionOffset+i*20,20));
            }
            g.EncodedOptions=d?.Options.Count??0;
            int known=0;g.KnownSources.Clear();
            for(int kind=0;kind<3;kind++)
            {
                var facts=kind==0?g.Knowledge.Center[actor]:g.Knowledge.Personal[kind==1?actor:1-actor];
                if(facts.Count==0)continue;
                foreach(var f in facts.OrderBy(f=>f.Min).ThenBy(f=>f.Max).ThenBy(f=>f.DefId,StringComparer.Ordinal))
                {
                    if(known>=Capacity)throw new InvalidOperationException("Remembered position capacity exceeded");
                    var row=obs.Slice(KnowledgeOffset+known++*8,8);row[0]=CardCode(f.DefId);row[1]=(kind+1)/3f;row[2]=f.Min/384f;row[3]=f.Max/384f;
                    row[4]=f.Min==f.Max&&!f.UncertainPresence?1:0;row[5]=f.UncertainPresence?1:0;row[6]=1;
                    row[7]=f.InstanceId>=0?(f.InstanceId+1)/65536f:0;
                    if(f.InstanceId>=0&&!f.UncertainPresence)g.KnownSources[f.InstanceId]=(known,f.DefId);
                }
            }
            // These identities were actually revealed or inferred from a known
            // top draw. Do not inspect the opposing hidden hand or expose its order.
            if(g.Knowledge.PublicHandIds[1-actor].Count!=0)
            foreach(var fact in g.Knowledge.PublicHandIds[1-actor].OrderBy(f=>f.Value,StringComparer.Ordinal).ThenBy(f=>f.Key))
            {
                if(known>=Capacity)throw new InvalidOperationException("Remembered position/hand capacity exceeded");
                var row=obs.Slice(KnowledgeOffset+known++*8,8);row[0]=CardCode(fact.Value);row[1]=4/3f;
                row[2]=row[3]=-1/384f;row[4]=row[5]=0;row[6]=1;row[7]=(fact.Key+1)/65536f;
            }
            obs[167]=known/384f;
            g.EncodedKnowledge=known;
            if(g.SelectionTrace.Count>Capacity)throw new InvalidOperationException("Selection trace capacity exceeded");
            for(int i=0;i<g.SelectionTrace.Count;i++)
            {
                var t=g.SelectionTrace[i];var row=obs.Slice(TraceOffset+i*4,4);row[0]=CardCode(t.option.DefId);row[1]=(t.ordinal+1)/384f;row[2]=t.amount/1000f;
                row[3]=entityMap.TryGetValue(t.option.CardInstanceId,out int entity)?entity/384f:0;
            }
            g.EncodedTrace=g.SelectionTrace.Count;
            obs[177]=g.SelectionTrace.Count/384f;
            int played=0;for(int relative=0;relative<2;relative++)
                foreach(var c in s.Players[relative==0?actor:1-actor].PlayedThisTurn)
                {
                    if(played>=Capacity)throw new InvalidOperationException("Played-this-turn capacity exceeded");
                    var row=obs.Slice(PlayedOffset+played++*4,4);row[0]=CardCode(c.DefId);row[1]=relative;row[2]=entityMap.TryGetValue(c.InstanceId,out int entity)?entity/384f:0;row[3]=c.FastPlayed?1:0;
                }
            obs[168]=played/384f;
            g.EncodedPlayed=played;
            Continuations(g,obs,entityMap);
            if(s.GameOver||g.Truncated){mask[0]=1;return;}
            for(int i=0;i<g.VisibleCount;i++){mask[i]=1;Candidate(g,g.Visible(i),entityMap,candidates.Slice(i*ActionDim,ActionDim));}
        }
        private static void Player(ShardsPlayer p,Span<float> row)
        {
            row[0]=p.Health/50f;row[1]=p.Mastery/30f;row[2]=p.Gems/20f;row[3]=p.Power/100f;row[4]=p.Hand.Count/64f;row[5]=p.Deck.Count/256f;
            row[6]=(Shards.AI.Encoder.HeroIndex(p.CharacterId)+1)/5f;row[7]=p.CharacterExhausted?1:0;row[8]=p.FocusedThisTurn?1:0;
            row[9]=p.HeroAbilityUsedThisTurn?1:0;row[10]=p.FirstBuyUsedThisTurn?1:0;row[11]=p.RelicRecruited?1:0;row[12]=p.DestinyTaken?1:0;
            row[13]=p.ExtraTurnUsed?1:0;row[14]=p.DoomGateFloodUsed?1:0;row[15]=p.Eliminated?1:0;row[16]=p.IgnoreShieldsThisTurn?1:0;
            row[17]=p.HealthToPowerThisTurn?1:0;row[18]=p.HealingDoubledThisTurn?1:0;row[19]=p.OverflowHealthToPowerThisTurn?1:0;
            row[20]=p.ShieldsDoubledUntilNextTurn?1:0;row[21]=p.NextRecruitsToHand/10f;row[22]=p.NextHomodeusChampionsIntoPlay/10f;
            row[23]=p.NextChampionsIntoPlay/10f;row[24]=p.CopyHomodeusAlliesThisTurn?1:0;row[25]=p.BonusDrawsOnBigHit/10f;
            row[26]=p.MaxDamageDealtToOneOpponent/100f;row[27]=p.CardsBanishedThisTurn/10f;row[28]=p.RerollsThisTurn/10f;row[29]=p.NextRerollDiscount/20f;
            row[30]=ShardsEngine.RerollCost(p)/20f;row[31]=p.FullControl?1:0;
            for(int faction=0;faction<7;faction++){row[32+faction]=p.FactionPlays((ShardsFaction)faction)/20f;row[39+faction]=p.FactionAllyPlays((ShardsFaction)faction)/20f;}
        }
        private static void Option(Adapter g,DecisionOption o,int ordinal,Dictionary<int,int> entities,Span<float> row)
        {
            row[0]=CardCode(o.DefId);row[1]=(ordinal+1)/384f;row[2]=entities.TryGetValue(o.CardInstanceId,out int entity)?entity/384f:0;
            row[3]=o.Amount/1000f;row[4]=o.Required?1:0;row[5]=Relative(o.OwnerIndex,g.Actor);row[6]=o.Disabled?1:0;
            row[7]=g.Selected.Contains(o.Id)?1:0;row[8]=g.Decision.DefaultOptionIds.Count(id=>id==o.Id)/1000f;
            if(o.Target.HasValue){var t=o.Target.Value;row[9]=(int)t.Kind+1;row[10]=t.Kind==TargetKind.Card?(entities.TryGetValue(t.A,out int target)?target/384f:0):t.Kind==TargetKind.Player?Relative(t.A,g.Actor):t.A/384f;row[11]=t.B/384f;}
            int hero=Shards.AI.Encoder.HeroIndex(o.DefId);if(hero>=0)row[9]=-(hero+1)/5f;
            if(o.DefId?.StartsWith(VolosAbilityChoice.FacePrefix,StringComparison.Ordinal)==true)
                row[10]=-(int.Parse(o.DefId.Substring(VolosAbilityChoice.FacePrefix.Length))+1)/4f;
            Text(o.Label,row.Slice(12,8),hero<0);
        }
        private static void Candidate(Adapter g,Candidate c,Dictionary<int,int> entities,Span<float> row)
        {
            row[c.Kind]=1;ShardsCard card=null;int slot=-1;int instance=c.Action switch{
                ShardsPlayCardAction a=>a.CardInstanceId,ShardsExhaustAction a=>a.CardInstanceId,ShardsAttackMonsterAction a=>a.CardInstanceId,
                ShardsTakeDestinyAction a=>a.CardInstanceId,ShardsRecruitRelicAction a=>a.CardInstanceId,_=>c.Option?.CardInstanceId??-1};
            if(c.Action is ShardsBuyCardAction buy)slot=buy.SlotIndex;if(c.Action is ShardsRerollRowAction reroll)slot=reroll.SlotIndex;
            if(slot>=0)card=g.Engine.State.CenterRow[slot];
            else if(entities.TryGetValue(instance,out int ordinal))
                // Iterator-held public reveals may be absent from the state's
                // current zones while lingering in a warm FindCard cache. The
                // public entity record is stable across cache invalidation and
                // search copies, and is exactly what this observation exposes.
                card=g.EntityBuffer[ordinal-1];
            string id=c.Option?.DefId??card?.DefId;row[16]=CardCode(id);
            if(CardIndex(id)>=0)
            {var def=ShardsCardDatabase.Get(id);row[17]=(int)def.Faction/6f;row[18]=(int)def.Type/6f;row[19]=def.Cost/13f;row[20]=(card==null?def.Defense:Defense(g,card))/50f;
                row[21]=(card==null?def.Shield:Shield(g,card))/20f;row[24]=g.Engine.EffectiveCost(g.Engine.State.Players[g.Actor],def)/13f;row[34]=def.ExhaustGemCost/20f;row[35]=def.Taunt?1:0;}
            if(card!=null){row[22]=card.Exhausted?1:0;row[23]=card.DamageThisTurn/50f;row[29]=((int)card.Zone+1)/16f;row[30]=card.FastPlayed?1:0;row[31]=card.BanishAtCleanup?1:0;}
            row[25]=slot>=0?(slot+1)/6f:(c.Ordinal+1)/384f;row[26]=(c.Option?.Amount??(c.Action as ShardsAttackMonsterAction)?.Amount??0)/1000f;
            row[27]=c.Option?.Required==true?1:0;row[28]=Relative(c.Option?.OwnerIndex??card?.Owner??-1,g.Actor);
            if(c.Kind==15){row[29]=c.Low/1000f;row[30]=c.High/1000f;}
            row[32]=entities.TryGetValue(instance,out int entity)?entity/384f:0;row[33]=(c.Ordinal+1)/384f;
            if(c.Option!=null){row[36]=(Shards.AI.Encoder.HeroIndex(c.Option.DefId)+1)/5f;
                if(c.Option.DefId?.StartsWith(VolosAbilityChoice.FacePrefix,StringComparison.Ordinal)==true)row[37]=(int.Parse(c.Option.DefId.Substring(VolosAbilityChoice.FacePrefix.Length))+1)/4f;
                Text(c.Option.Label,row.Slice(38,8),Shards.AI.Encoder.HeroIndex(c.Option.DefId)<0);}
            row[46]=g.Engine.State.Players[g.Actor].CharacterId=="kosynwu"&&c.Kind==9?1:0;row[47]=c.Option?.Disabled==true?1:0;
        }
        private static int Defense(Adapter g,ShardsCard c)=>c.Owner>=0&&c.Def.IsChampion?g.Engine.EffectiveDefense(g.Engine.State.Players[c.Owner],c):c.Def.Defense;
        private static int Shield(Adapter g,ShardsCard c)=>c.Owner>=0?g.Engine.ShieldValue(g.Engine.State.Players[c.Owner],c):c.Def.Shield;
        private static void Text(string text,Span<float> output,bool includeNumbers=true)
        {
            uint hash=2166136261;int number=0,numbers=4;bool digits=false;
            foreach(char ch in text??"")
            {hash=unchecked((hash^ch)*16777619);if(includeNumbers&&ch>='0'&&ch<='9'){number=checked(number*10+ch-'0');digits=true;}
                else if(digits){if(numbers>=8)throw new InvalidOperationException("Decision text has too many numeric values: "+text);output[numbers++]=number/1000f;number=0;digits=false;}}
            if(digits){if(numbers>=8)throw new InvalidOperationException("Decision text has too many numeric values: "+text);output[numbers]=number/1000f;}
            string previous=TextIdentities.GetOrAdd(hash,text??"");if(previous!=(text??""))throw new InvalidOperationException("Decision text identity collision");
            for(int b=0;b<4;b++)output[b]=((hash>>(8*b))&255)/255f;
        }
        private static void Continuations(Adapter g,Span<float> obs,Dictionary<int,int> entities)
        {
            var ctx=ActiveContext.GetValue(g.Engine) as ShardsContext;
            float activeSource=ctx?.Source==null?0:PublicReference(g,entities,ctx.Source.InstanceId,ctx.Source.DefId);
            if(activeSource!=0)
            {
                obs[169]=Relative(ctx.ControllerIndex,g.Actor);obs[170]=CardCode(ctx.Source.DefId);obs[171]=activeSource;
                PublicCopies(g,ctx,obs,entities);
            }
            else g.EncodedCopy=0;
            var targets=SplitTargets.GetValue(g.Engine) as List<int>;var amounts=SplitAmounts.GetValue(g.Engine) as List<int>;
            if(targets!=null&&amounts!=null)for(int i=0;i<targets.Count;i++)if(targets[i]>=0&&targets[i]<2)obs[172+Relative(targets[i],g.Actor)]=amounts[i]/1000f;
            var queue=Queue.GetValue(g.Engine) as Queue<(IShardsEffect effect,ShardsContext ctx)>;
            int index=0;
            foreach(var q in queue)
            {
                // Queue length/unknown source/delegate locals can depend on hidden
                // effects. Emit only entries with a currently public source.
                float source=q.ctx.Source==null?0:PublicReference(g,entities,q.ctx.Source.InstanceId,q.ctx.Source.DefId);
                if(source==0)continue;
                if(index>=64)throw new InvalidOperationException("Public continuation capacity exceeded");
                var row=obs.Slice(ContinuationOffset+index++*8,8);row[0]=CardCode(q.ctx.Source.DefId);row[1]=Relative(q.ctx.ControllerIndex,g.Actor);row[2]=source;
                row[3]=ReferenceEquals(q.effect,q.ctx.Source.Def.PlayEffect)?1:0;row[4]=ReferenceEquals(q.effect,q.ctx.Source.Def.ExhaustEffect)?1:0;
                row[5]=ReferenceEquals(q.effect,q.ctx.Source.Def.RewardEffect)?1:0;row[6]=ReferenceEquals(q.effect,q.ctx.Source.Def.MonsterAttackEffect)?1:0;row[7]=q.ctx.Source.Def.IsMonster?1:0;
            }
            obs[174]=index/64f;
            obs[175]=g.Engine.IsResolvingEndTurn?1:0;
            g.EncodedQueue=index;
        }
        private static float PublicReference(Adapter g,Dictionary<int,int> entities,int instance,string def)
        {
            if(entities.TryGetValue(instance,out int entity))return entity/384f;
            if(g.KnownSources.TryGetValue(instance,out var fact)&&fact.def==def)return -fact.ordinal/384f;
            return 0;
        }
        private static void PublicCopies(Adapter g,ShardsContext ctx,Span<float> obs,Dictionary<int,int> entities)
        {
            int at=0;
            foreach(var selection in ctx.PublicSelections)
            {
                var row=CopyRow(obs,ref at);row[0]=CardCode(selection.DefId);row[1]=selection.Context=="soi.mode"?0.5f:0.25f;
                row[2]=PublicReference(g,entities,selection.InstanceId,selection.DefId);row[3]=CardCode(ctx.Source.DefId);
                if(selection.Context=="soi.mode")Text(selection.Label,row.Slice(4,4),includeNumbers:false);
                else{row[4]=(selection.Ordinal+1)/384f;row[5]=at/96f;row[7]=PendingOption(g,selection.InstanceId);}
            }
            foreach(var card in ctx.PublicCopiedCards)
            {
                var row=CopyRow(obs,ref at);row[0]=CardCode(card.DefId);row[1]=0.75f;row[2]=PublicReference(g,entities,card.InstanceId,card.DefId);
                row[3]=CardCode(ctx.Source.DefId);row[4]=at/96f;row[7]=PendingOption(g,card.InstanceId);
            }
            for(int scope=0;scope<ctx.PublicCopyScopes.Count;scope++)
            {
                var frame=ctx.PublicCopyScopes[scope];
                for(int target=0;target<frame.Cards.Count;target++)
                {
                    var card=frame.Cards[target];var row=CopyRow(obs,ref at);row[0]=CardCode(card.DefId);row[1]=1;
                    row[2]=PublicReference(g,entities,card.InstanceId,card.DefId);row[3]=CardCode(ctx.Source.DefId);
                    row[4]=(scope+1)/96f;row[5]=(target+1)/96f;row[6]=target.CompareTo(frame.CurrentIndex);row[7]=target==frame.CurrentIndex?frame.RemainingCopies/10f:0;
                }
            }
            obs[176]=at/96f;g.EncodedCopy=at;
        }
        private static Span<float> CopyRow(Span<float> obs,ref int at)
        {
            if(at>=96)throw new InvalidOperationException("Public copy provenance capacity exceeded");
            return obs.Slice(CopyOffset+at++*8,8);
        }
        private static float PendingOption(Adapter g,int instance)
        {
            if(g.Decision==null)return 0;
            int ordinal=g.Decision.Options.FindIndex(o=>o.CardInstanceId==instance);
            return (ordinal+1)/384f;
        }
    }
}
namespace Shards.Preflight
{
    // The linked static descriptor exporter uses this catalog-only compatibility name.
    internal static class Encoder { internal static string[] CardIds=>Shards.ZeroDepth.Encoder.CardIds; }
}
