using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Shards.Engine;

namespace Shards.Preflight
{
    // Static, PUBLIC card definitions only. No live state or private zones are read.
    // A deliberately lossy phase/operation summary, NOT an executable rules grammar.
    // Primary sources: Assets/Scripts/Shards/Engine/ShardsEffects.cs (typed fields),
    // ShardsDuelEffects.cs (Duel primitives), ShardsTypes.cs (static hooks), and
    // Assets/Scripts/Shards/Content/Shards{Base,Relics,Shadow,Horizon,Duel}Set.cs
    // (the explicitly reviewed Custom callback operations/quantities below).
    // Behavioral probes are pure public synthetic fixtures, never hidden live state.
    // Declared limitation: non-resource operation gates and nested branch binding
    // are pooled; these descriptors augment preserved learned weights and do not
    // constitute a theorem that two different card programs are distinguishable.
    internal static class EffectDescriptors
    {
        internal const int Width = 512;
        internal const string Schema = "shards-public-effect-summary-v1";
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly string[] PhaseNames = { "play", "exhaust", "reward", "monster_attack" };
        private static readonly string[] Features = {
            "gems", "power", "mastery", "health", "draw",
            "conditional_gems", "conditional_power", "conditional_mastery", "conditional_health", "conditional_draw",
            "per_unit_gems", "per_unit_power", "per_unit_mastery", "per_unit_health", "per_unit_draw",
            "banish", "return_cards", "destroy_champion", "free_fast_play", "free_recruit", "copy_effect",
            "self_health_loss", "enemy_health_loss", "enemy_mastery_loss", "discard", "scry_center", "reorder_center",
            "reroll_discount", "reset_champion", "extra_turn", "search_deck", "reveal", "grant_destiny", "grant_relic", "monster_operation",
            "cost_limit", "count_limit", "choice", "optional", "all_targets", "to_hand", "to_deck_top", "keep_fast_play",
            "ignore_shields", "health_to_power", "double_healing", "double_shields", "end_turn_bonus_draw", "next_champion_into_play", "next_recruit_to_hand",
            "mastery_gate", "mastery_threshold_sum", "best_mastery_tiers", "faction_gate", "faction_required_sum", "allegiance_gate", "unify_gate", "dominion_gate",
            "condition_champions", "condition_discard", "condition_played", "condition_hand", "condition_health", "condition_mastery", "condition_distinct_factions", "condition_card_cost",
            "faction_homodeus", "faction_undergrowth", "faction_order", "faction_wraethe", "faction_aion", "condition_shield", "condition_monsters", "condition_character", "condition_highest_mastery",
            "composite_parts", "callback_count", "captured_numeric_sum", "captured_numeric_abs_sum", "callback_branch_count",
            "literal_1", "literal_2", "literal_3", "literal_4", "literal_5", "literal_6", "literal_7", "literal_8", "literal_10", "literal_15", "literal_20", "literal_30", "literal_40", "literal_50", "literal_100", "literal_1000000"
        };
        private static readonly double[] Literals = {1,2,3,4,5,6,7,8,10,15,20,30,40,50,100,1000000};
        private static readonly Dictionary<short, OpCode> Opcodes = typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static)
            .Where(f=>f.FieldType==typeof(OpCode)).Select(f=>(OpCode)f.GetValue(null)).ToDictionary(x=>x.Value);
        private static readonly SortedDictionary<string,string> Checksums = new(StringComparer.Ordinal);
        private static readonly SortedDictionary<string,int> Types = new(StringComparer.Ordinal);
        private static readonly HashSet<string> RecipesUsed = new(StringComparer.Ordinal);
        private static int Cards, Callbacks;
        private static bool EnforceManifest;
        // Generated once from the explicitly inspected callback graph. Hashes are
        // audit metadata ONLY, never policy inputs. Unknown/changed code fails closed.
        private const string ApprovedManifest = @"Shards.Content.ShardsBaseSet+<>c::Boolean <DecurionCopy>b__7_0(Shards.Engine.ShardsCard)|c2d20214c7f2c5bdc53da872c677715f9a47aafd7ab45f91be8780fe8c4f0949
Shards.Content.ShardsBaseSet+<>c::Boolean <RegisterHomodeus>b__6_0(Shards.Engine.ShardsState, Shards.Engine.ShardsPlayer, Shards.Engine.ShardsPlayer, Shards.Engine.ShardsCard)|052afb5ebe956d084a1ac29f77b286ba54316b92036d0cf52c3378057b7d035c
Shards.Content.ShardsBaseSet+<>c::Boolean <RegisterHomodeus>b__6_2(Shards.Engine.ShardsCardDef)|48fa77fe1c02a958f205201796320b0f58ceb2acf28a9a8a91f3ca9720b2d50b
Shards.Content.ShardsBaseSet+<>c::Boolean <RegisterHomodeus>b__6_4(Shards.Engine.ShardsContext)|09d3ac04247b4e73e3938baabdf69ab8832c90ef999195ab9f5821c179c1c9c0
Shards.Content.ShardsBaseSet+<>c::Boolean <RegisterHomodeus>b__6_5(Shards.Engine.ShardsCard)|5eecc2dac91be325f8cc9090425c9aad6cadb70be2ae149157ba70db451a1cd1
Shards.Content.ShardsBaseSet+<>c::Boolean <RegisterUndergrowth>b__9_0(Shards.Engine.ShardsCardDef)|12c31d61010f3d436341d19c6a10a05bd7e0b481f300358e38394ad9b010a885
Shards.Content.ShardsBaseSet+<>c::Boolean <RegisterUndergrowth>b__9_1(Shards.Engine.ShardsCardDef)|856a8d8367496b24b372f2afa4e3385d5eda73313dbf5587c1f3f079c12613bf
Shards.Content.ShardsBaseSet+<>c::Boolean <RegisterWraethe>b__10_1(Shards.Engine.ShardsState, Shards.Engine.ShardsPlayer, Shards.Engine.ShardsPlayer, Shards.Engine.ShardsCard)|e59a70fe6f40650adbf15cd26051dc65377540fd729a979fa70c1e687caa79c6
Shards.Content.ShardsBaseSet+<>c::Boolean <RegisterWraethe>b__10_3(Shards.Engine.ShardsCardDef)|6053688fdf20b7373c2335b625b24652a69be48235937d5ef9f5aa8e8441f35a
Shards.Content.ShardsBaseSet+<>c::Boolean <RegisterWraethe>b__10_4(Shards.Engine.ShardsCardDef)|ca2fd9be4989a54c8f553515e5b6c769d9bee2648cf9892e68e5526b294d193e
Shards.Content.ShardsBaseSet+<>c::Int32 <RegisterHomodeus>b__6_1(Shards.Engine.ShardsContext)|7fb06c238535325a5436161e25c766bb41143ea1e8d6bf3bd606ac0b6d413348
Shards.Content.ShardsBaseSet+<>c::Int32 <RegisterUndergrowth>b__9_2(Shards.Engine.ShardsContext)|05021e69d3e7d072288b0125f2c3c9e683be025eb18cef235a73e2b1de6f8723
Shards.Content.ShardsBaseSet+<>c::Int32 <RegisterWraethe>b__10_2(Shards.Engine.ShardsContext)|6911b2a1a6bd4bea41cd7cb2f798698d17524906a95aa435b06bedb9e0b9be7c
Shards.Content.ShardsBaseSet+<>c::Void <RegisterHomodeus>b__6_3(Shards.Engine.ShardsContext)|e3a700ef03bc7e5e9a86a6335235c925c511d0c592f3284965243b3bb790255b
Shards.Content.ShardsBaseSet+<>c::Void <RegisterWraethe>b__10_0(Shards.Engine.ShardsContext)|db5f6a3c263767a0ee13e062af3ca8932966ffabd3719802ee9168319f098f87
Shards.Content.ShardsBaseSet+<DecurionCopy>d__7::Boolean MoveNext()|a70620e060583bb7fcacb8abb3089ec93eca56beccc8f697ae11bc9b44ae38a9
Shards.Content.ShardsBaseSet+<DecurionCopy>d__7::Void <>m__Finally1()|d54d769ccc8caec64efd626f998f270debc30881990be197049a55c3bc05e062
Shards.Content.ShardsBaseSet+<DecurionCopy>d__7::Void <>m__Finally2()|89ea284e796723cc362bae47306b928c857bbf85feefe80b3a816631c360d3f0
Shards.Content.ShardsBaseSet+<DecurionCopy>d__7::Void System.IDisposable.Dispose()|1cc852f4649c0df6bd3ea8a22f037453ca2ee0105f986d965e5ed8f6725da7d1
Shards.Content.ShardsBaseSet::Int32 CountChampions(Shards.Engine.ShardsContext, Shards.Engine.ShardsFaction)|3e43bac05dacb3db2251b70b8620b4a26d361e2abd5c6712d6b38338ebfaad95
Shards.Content.ShardsBaseSet::Int32 CountDiscard(Shards.Engine.ShardsContext, Shards.Engine.ShardsFaction)|e4aec97965a51d40cdf46172b8c7a23df4135188ff0a91bd59892d541ce10bee
Shards.Content.ShardsBaseSet::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] DecurionCopy(Shards.Engine.ShardsContext)|cbd561ff61ffc20928315475457ce538678e80e913d642e6f5a3790ac8054629
Shards.Content.ShardsDuelSet+<>c::Boolean <RegisterBespokeErrata>b__13_4(Shards.Engine.ShardsContext)|6b23b830f023058b2bcba0e2f9c2695b803d20c67397b3a23ee56e10dc341f93
Shards.Content.ShardsDuelSet+<>c::Boolean <RegisterCards>b__8_3(Shards.Engine.ShardsContext)|769eb6fe27b4d0009cce6d2ad77cee50fb8663fa442946084ec7eb7f9f44b1e1
Shards.Content.ShardsDuelSet+<>c::Boolean <RegisterDestinyErrata>b__14_0(Shards.Engine.ShardsContext)|63f973471a3834f6117d630f9e0a60452875b9322fee828f83a5531c2eac018f
Shards.Content.ShardsDuelSet+<>c::Boolean <RegisterDestinyErrata>b__14_1(Shards.Engine.ShardsContext)|8648c683a1b31f85fa5add0832297aa1516745a7d4aca0ec6938724d347da9b7
Shards.Content.ShardsDuelSet+<>c::Boolean <RegisterDestinyErrata>b__14_2(Shards.Engine.ShardsContext)|c397c7d66cfbf7afa8c3711cea4638e8502d14b89105264c5282dacfc2850e83
Shards.Content.ShardsDuelSet+<>c::Boolean <RegisterDestinyErrata>b__14_3(Shards.Engine.ShardsContext)|a98b8914c1c5bb2e6c6cbe06681f7b77ff299e53bee8f00ddbca153e97957c60
Shards.Content.ShardsDuelSet+<>c::Boolean <RegisterDestinyErrata>b__14_4(Shards.Engine.ShardsContext)|0d174754a81b4628bacf4636713b89edb6e6426f7cf9113df1d6267120c6beff
Shards.Content.ShardsDuelSet+<>c::Boolean <RegisterDestinyErrata>b__14_5(Shards.Engine.ShardsContext)|f0b916b2b543a56282d9b3a96b3cac32c6e3200f95c507186f9e647673a2efbf
Shards.Content.ShardsDuelSet+<>c::Boolean <RegisterDestinyErrata>b__14_8(Shards.Engine.ShardsCard)|69119804059cd9c2cd888d2b62995f731ca138d5c6525fe43eb004b34c37f0ae
Shards.Content.ShardsDuelSet+<>c::Boolean <RegisterDestinyErrata>b__14_9(Shards.Engine.ShardsCard)|dd993a72a03213058255925a33242c73cf07d831fa21735d60decf102b044d59
Shards.Content.ShardsDuelSet+<>c::Boolean <RegisterHookErrata>b__12_1(Shards.Engine.ShardsContext)|5d43b30fc33b814b8f8b881ebb23e42954bcea5b4254ce8131211c2ab048a6e9
Shards.Content.ShardsDuelSet+<>c::Boolean <RegisterHookErrata>b__12_2(Shards.Engine.ShardsState, Shards.Engine.ShardsPlayer, Shards.Engine.ShardsPlayer, Shards.Engine.ShardsCard)|d13b63e155f6bad9465d7f4c27af3211015773d22572b706f74ef6566870dbf0
Shards.Content.ShardsDuelSet+<>c::Boolean <RegisterStatErrata>b__11_0(Shards.Engine.ShardsCardDef)|91d2164b65f7ce4be7bf3dbc4766974e4f1b07cb2c341fe2958d768c3e8903ae
Shards.Content.ShardsDuelSet+<>c::Int32 <GrimTutor>b__25_0(Shards.Engine.ShardsCard, Shards.Engine.ShardsCard)|32ab416ada778b7ca2196f6486e3848bfbd52031880b9d2117e724ab3877faad
Shards.Content.ShardsDuelSet+<>c::Int32 <RegisterAllegianceErrata>b__10_0(Shards.Engine.ShardsContext)|03b9aa97ba06d4e34f9e3aef7e2511a5a0825332abf09e8ff006843940c9c77e
Shards.Content.ShardsDuelSet+<>c::Int32 <RegisterAllegianceErrata>b__10_1(Shards.Engine.ShardsPlayer, Shards.Engine.ShardsCard, Shards.Engine.ShardsCard)|0fa5b9c76116b0ef8738efd494d483e90d393f365921697ddab7e109b432340c
Shards.Content.ShardsDuelSet+<>c::Int32 <RegisterBespokeErrata>b__13_1(Shards.Engine.ShardsPlayer)|ad5c48bfb49840e7cda42f012f23864985c2be109b42121b26d2ff9de1e57f4e
Shards.Content.ShardsDuelSet+<>c::Int32 <RegisterBespokeErrata>b__13_2(Shards.Engine.ShardsPlayer)|f990762ce6ae984bfb44c9deef46448521ba3e2f36d7cd10e5c361d0809115b0
Shards.Content.ShardsDuelSet+<>c::Int32 <RegisterBespokeErrata>b__13_3(Shards.Engine.ShardsPlayer)|54241aa197de6a26d192ac5aff242b1e9d43565724dee9e74c2772d9cccd156e
Shards.Content.ShardsDuelSet+<>c::Int32 <RegisterHookErrata>b__12_0(Shards.Engine.ShardsContext)|206bce5adb1fac7a598e370cac6beddf5c1eccb10dea642d2e9e9eb2f538495f
Shards.Content.ShardsDuelSet+<>c::Int32 <RegisterHookErrata>b__12_3(Shards.Engine.ShardsPlayer)|e9566c289c1899c7908a1cf9217358904c8aa90d5f44a4a2bd90890b28247d9e
Shards.Content.ShardsDuelSet+<>c::Int32 <RegisterRelics>b__7_0(Shards.Engine.ShardsContext)|39b44ee960f4085bd830ec64f21dc87d6212cf802dcf475ccc03edcd2a517089
Shards.Content.ShardsDuelSet+<>c::Int32 <RegisterRelics>b__7_1(Shards.Engine.ShardsContext)|a80c295f323b856b27fd2b9494158aa0e170b00955e758ec091b27246b72b156
Shards.Content.ShardsDuelSet+<>c::Int32 <RegisterRelics>b__7_2(Shards.Engine.ShardsContext)|0668624d46bfb08e492f95d90ed4bd5c2bb11beba6b33fc1351d314094eda38d
Shards.Content.ShardsDuelSet+<>c::Int32 <RegisterStatErrata>b__11_2(Shards.Engine.ShardsContext)|4c3957f0debf551865d8d5c9365f7e901d54dc0596eed56db53cc4f810874e18
Shards.Content.ShardsDuelSet+<>c::Int32 <RegisterStatErrata>b__11_3(Shards.Engine.ShardsContext)|69fa3a8009a5b5dc06ef39996108fb8f679fee676ef7e0f357872e60a78af0b6
Shards.Content.ShardsDuelSet+<>c::Int32 <WorldPiercerDuel>b__22_0(Shards.Engine.ShardsCard, Shards.Engine.ShardsCard)|93c8fd4bed1917d35d3e35ae87f7bb11aec8d8333f637d6a2f23cee307acfea3
Shards.Content.ShardsDuelSet+<>c::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] <RegisterBespokeErrata>b__13_5(Shards.Engine.ShardsContext)|1fb3ad653748bae6f08a942b3f275488b059946bd0ac28c7b4e0c332d8008dac
Shards.Content.ShardsDuelSet+<>c::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] <RegisterDestinyErrata>b__14_6(Shards.Engine.ShardsContext)|fdd6c4c4b45899f3dc93c6bd4283161aa07053fd1d8bc65e0d998fac0e9741be
Shards.Content.ShardsDuelSet+<>c::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] <RegisterDestinyErrata>b__14_7(Shards.Engine.ShardsContext)|2061dea462676238c51aead6adcadce877863521b89ab6c24859858f04269e9f
Shards.Content.ShardsDuelSet+<>c::Void <RegisterBespokeErrata>b__13_0(Shards.Engine.ShardsContext)|4e2af523eb2408c250d8e2d7dfaa375dec0010b9f68d0f2f4fea1876faf8c5cb
Shards.Content.ShardsDuelSet+<>c::Void <RegisterCards>b__8_0(Shards.Engine.ShardsContext)|c6d55d6c659b726894dccb5beafd570a6aa9a41f29b8ed8dcbd3661a87bb21da
Shards.Content.ShardsDuelSet+<>c::Void <RegisterCards>b__8_1(Shards.Engine.ShardsContext)|2d76db9cce0ae9334c32309259e100a52d25a397800089cd75242b11e35f8864
Shards.Content.ShardsDuelSet+<>c::Void <RegisterCards>b__8_2(Shards.Engine.ShardsContext)|18d625028fcab54241017c9b11b230dfefe583ef22f0756ab760eb500b0f5d23
Shards.Content.ShardsDuelSet+<>c::Void <RegisterHookErrata>b__12_4(Shards.Engine.ShardsContext)|904c11e002556f14046d75c9a62812a02abe7e35ef727b8a2245efd8c5e27271
Shards.Content.ShardsDuelSet+<>c::Void <RegisterStatErrata>b__11_1(Shards.Engine.ShardsContext)|76187f8f38efae3c21c5f9a5bce1857b26e3b6f1913ab27a076ef6389734ca4f
Shards.Content.ShardsDuelSet+<>c__DisplayClass13_0::Boolean <RegisterBespokeErrata>b__6(Shards.Engine.ShardsCard)|9d95056a29b0adceba837f169dd49baf05023b6ef1e931b406ed881be952e439
Shards.Content.ShardsDuelSet+<>c__DisplayClass18_0::Boolean <RiposteBonus>b__0(Shards.Engine.ShardsCard)|614ac3c5dd158eda32ab818f8255e9a11bf6633788365626540abab366d756ca
Shards.Content.ShardsDuelSet+<>c__DisplayClass18_0::Boolean <RiposteBonus>b__1(Shards.Engine.ShardsCard)|cef0eb28791e4ff0454438b6f88ce844907dc660f0bbbd57b1d5419ff688787d
Shards.Content.ShardsDuelSet+<>c__DisplayClass18_0::Boolean <RiposteBonus>b__2(Shards.Engine.ShardsCard)|e01eef67528a480a6e739d76f3f5ae939e196442a64c5c1fd861f9933ffd6f32
Shards.Content.ShardsDuelSet+<>c__DisplayClass19_0::Boolean <WarpquartzDuel>b__0(Shards.Engine.ShardsCard)|b8ab63b6481d01856d8bb827718cbb861d94a43a904db3f1a1143103165e2bc6
Shards.Content.ShardsDuelSet+<>c__DisplayClass20_0::Boolean <FabricatorDuel>b__0(Shards.Engine.ShardsCard)|66b9b96bc84cd3df8294fbff4d2be2f6063414eab1939391aa9d1ade3f7df0bd
Shards.Content.ShardsDuelSet+<>c__DisplayClass20_1::Boolean <FabricatorDuel>b__1(Shards.Engine.ShardsCard)|8afac0b8a81697af0838f4da99a2c6197034ef09c08b15729c57efd95e2b7051
Shards.Content.ShardsDuelSet+<>c__DisplayClass21_0::Boolean <DashDuel>b__0(Shards.Engine.ShardsCard)|2b3d6eab4d97bd99028f229ce46eb362446ad85ef9d10c62b08db98ae63dbe03
Shards.Content.ShardsDuelSet+<>c__DisplayClass21_0::Boolean <DashDuel>b__1(Shards.Engine.ShardsCard)|7a212828a2bc29838ee76ff0f6187fdbbd383f942b60f832df6d5271dfb18d39
Shards.Content.ShardsDuelSet+<>c__DisplayClass22_0::Boolean <WorldPiercerDuel>b__1(Shards.Engine.ShardsCard)|ff6e046af19d980e7049091f003744923361d12bb77537687e4cd4913d8048bf
Shards.Content.ShardsDuelSet+<>c__DisplayClass25_0::Boolean <GrimTutor>b__1(Shards.Engine.ShardsCard)|0feca17eb4f9896d8dbba409fdd79bde75666dcee6585dc1da12b7f4eab2e269
Shards.Content.ShardsDuelSet+<>c__DisplayClass27_0::Boolean <DoomGateDestroy>b__0(Shards.Engine.ShardsCard)|660076343c4ca5cc41a1c37d5a35b4b8df2041688207f2bdb4b5d98557136526
Shards.Content.ShardsDuelSet+<>c__DisplayClass28_0::Boolean <Longshot>b__0(Shards.Engine.ShardsCard)|0d41e077f48fa9ee49e8c13e2f42effcfddec1cf3d138ec508c622f594d10063
Shards.Content.ShardsDuelSet+<>c__DisplayClass28_1::Boolean <Longshot>b__1(Shards.Engine.ShardsCard)|d651794b742fc6dab0c2606b04a04c9280c61dd083247ca3cd11b7f50a77f1c6
Shards.Content.ShardsDuelSet+<>c__DisplayClass30_0::Boolean <BleakCommunion>b__0(Shards.Engine.ShardsCard)|01d58f9f31efe0902fbcb3ad42ad433f59c1da5743bba56dc47778d5838086f2
Shards.Content.ShardsDuelSet+<BleakCommunion>d__30::Boolean MoveNext()|ee8e65a01e40d3a8f591470eeb7fc0120af7dd12f2f6f37d3b8dce5e65a4022e
Shards.Content.ShardsDuelSet+<DashDuel>d__21::Boolean MoveNext()|bfac1f59adb05cffc03165c92060e600f95a3d58141e2e6605907528f4158512
Shards.Content.ShardsDuelSet+<DeadlyRecruitsDuel>d__24::Boolean MoveNext()|4f00fe5838f138b9dae45c76acd68a6646a1bd6e1caeb69ee038fbf87316af48
Shards.Content.ShardsDuelSet+<DestroyOpponent>d__29::Boolean MoveNext()|377b4dab91829b3bdf8fbf2cae15d51e52effc0004fb825193753996d11393b7
Shards.Content.ShardsDuelSet+<DoomGateDestroy>d__27::Boolean MoveNext()|b89b8f15339f17e2b8504cbd0ff6dc96406f0075bef1abef3021179591c45929
Shards.Content.ShardsDuelSet+<DoomGatePlay>d__26::Boolean MoveNext()|8636e14ec86dc181cdc445d8cdfbb25f799dc3f9a16ac0c38a1ede73f9ef9bd4
Shards.Content.ShardsDuelSet+<ExtraTurn>d__15::Boolean MoveNext()|24fc76fe013dd5a45eeeb90f41f41bcc38b6ee375b74c346ea341d889885bd5c
Shards.Content.ShardsDuelSet+<FabricatorDuel>d__20::Boolean MoveNext()|5093151ad0fa62fc66646490f99de72748d016fbb3e2a019ab7542dd2a39965c
Shards.Content.ShardsDuelSet+<FabricatorDuel>d__20::Void <>m__Finally1()|0642b1f00655cf865b8054c90ac4b6132889ea2a7e46cc0819b09db52a52fc40
Shards.Content.ShardsDuelSet+<FabricatorDuel>d__20::Void <>m__Finally2()|5a720c993e989b3591967497bc45ea3f3ee951c6a4d7b3d1e6caeef7d7ee5def
Shards.Content.ShardsDuelSet+<FabricatorDuel>d__20::Void System.IDisposable.Dispose()|7b7f818248ee31cfa968cf99167d288e807054fd02b9e651c5299b783aa7efd3
Shards.Content.ShardsDuelSet+<GrimTutor>d__25::Boolean MoveNext()|db3f1cd068d23c9dfa07e99a95ef22d4110cddab366d60cece1e99a8f0c2b931
Shards.Content.ShardsDuelSet+<Longshot>d__28::Boolean MoveNext()|08de66245ae134e9e221086a88fa742e1a4d9d05e3dc3a0f831cf1fc062f5978
Shards.Content.ShardsDuelSet+<ReactorChoice>d__16::Boolean MoveNext()|26539bcf864ed2a24728a44868426fab347756300a0a1b2b767d73f1a1f1dd94
Shards.Content.ShardsDuelSet+<RemoveFromShop>d__17::Boolean MoveNext()|2743f2b571edb8637647941ab404db90955dafb56845923b2a8303da2d70756d
Shards.Content.ShardsDuelSet+<RiposteBonus>d__18::Boolean MoveNext()|5ca2886c4b4d59ac6fef2417a119d0c7419da4f63a6af170b13d2a471bde7d2c
Shards.Content.ShardsDuelSet+<WarpquartzDuel>d__19::Boolean MoveNext()|945da0fe78a6f8cc1462d468eb75b9ed4a0fb29bc9ff48c81f8ea079cd49e103
Shards.Content.ShardsDuelSet+<WarpquartzDuel>d__19::Void <>m__Finally1()|e5a60b954ddb587734edd53045fa463c46f555b2b0afdfeef150e9dc5b667745
Shards.Content.ShardsDuelSet+<WarpquartzDuel>d__19::Void <>m__Finally2()|853fb6e181db0f273be27501d6029071954cf0a57dcb9a0234a539e4532b824c
Shards.Content.ShardsDuelSet+<WarpquartzDuel>d__19::Void System.IDisposable.Dispose()|b610f4365f14c6ad0c97710bcf06b170f359b8d9f8c7f4d56c1d49ba516645e5
Shards.Content.ShardsDuelSet+<WorldPiercerDuel>d__22::Boolean MoveNext()|10a8bf3ea5e94e041ca073e8e37cf7665592b3906e74873a355a7e42f52d9ca7
Shards.Content.ShardsDuelSet::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] BleakCommunion(Shards.Engine.ShardsContext)|1c07587356b0f4fff7599ef0ec531d7e643d7caf59cfac7fa5b24ba47c8fab49
Shards.Content.ShardsDuelSet::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] DashDuel(Shards.Engine.ShardsContext)|a4bdcd6bc70ecc76f75707f0e28549f3f6d27be20cc54f42fbba759f2398d620
Shards.Content.ShardsDuelSet::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] DeadlyRecruitsDuel(Shards.Engine.ShardsContext, Int32)|8dbe1a28414a86c89fc6ab0a4880e53922161d2cc49246a0d0560b5fa74763b7
Shards.Content.ShardsDuelSet::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] DestroyOpponent(Shards.Engine.ShardsContext)|e322a5aecedd30cdd6ac7555c52969cfd865b2bb5a7520e185df6d032bc7e284
Shards.Content.ShardsDuelSet::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] DoomGateDestroy(Shards.Engine.ShardsContext)|7d6183f3a361934e94421d3a56dee1fefedb39ae4ee1cbffae2222c06973cffb
Shards.Content.ShardsDuelSet::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] DoomGatePlay(Shards.Engine.ShardsContext)|297370c84c6aca7e51ac2a0223a07ecb372d162d80458034951e2ac4425f7f28
Shards.Content.ShardsDuelSet::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] ExtraTurn(Shards.Engine.ShardsContext)|9fe6c40125a09df46ad633af5fece3396a9093ac7bc1acfdd1527b9eccb678b7
Shards.Content.ShardsDuelSet::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] FabricatorDuel(Shards.Engine.ShardsContext)|e21d4c3881467c36931b0cd5656a3774381c70fbcd00e6b6a88b6b276c8d363c
Shards.Content.ShardsDuelSet::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] GrimTutor(Shards.Engine.ShardsContext)|9d045988ba2811d36a46e66af61a066a52d0afcd1acb8cb8c2cf77d4f34ad43e
Shards.Content.ShardsDuelSet::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] Longshot(Shards.Engine.ShardsContext)|3585fe5ede3f0e93b412959e01a25748cd313096987a1f143ca0c203730aef2b
Shards.Content.ShardsDuelSet::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] ReactorChoice(Shards.Engine.ShardsContext)|bb692f06cf9d11c2a481aac2e03d7e99d8cbe7d46f0f87e5e5bac15692666e0f
Shards.Content.ShardsDuelSet::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] RemoveFromShop(Shards.Engine.ShardsContext)|05cc27093d10f2faffe26b757599cd6dce3e680e03947988941f3eef7f9ff338
Shards.Content.ShardsDuelSet::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] RevealTopForChampion(Shards.Engine.ShardsContext, Int32)|640eb6cd6215dde175de93cc5dd6174009796fe37266b1e4edbfd52a9840db7e
Shards.Content.ShardsDuelSet::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] RiposteBonus(Shards.Engine.ShardsContext)|aa7d4a8b69a0714478a609576efcc829a4133f1cdf7e7b9a203288e8a7f6a907
Shards.Content.ShardsDuelSet::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] WarpquartzDuel(Shards.Engine.ShardsContext)|8dac973acc20576c2616dc7b1375d9ba666d755c5f4c9024c7cd4a3284079ddc
Shards.Content.ShardsDuelSet::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] WorldPiercerDuel(Shards.Engine.ShardsContext)|eca522fcb9b958abd9ddd24919a2c8a55bf393156165c6c3c265dee3d6e955e1
Shards.Content.ShardsHorizonSet+<>c::Boolean <BloodForBloodFlow>b__18_0(Shards.Engine.ShardsCard)|5654c87903928b774a24cae0029f1a47b3cce512d613e8dee6ac79a71d392512
Shards.Content.ShardsHorizonSet+<>c::Boolean <CorruptionReward>b__15_0(Shards.Engine.ShardsCard)|cafee538488c43695d70181f2c54993d4902ccf3b68c3267d760ab0c116f5761
Shards.Content.ShardsHorizonSet+<>c::Boolean <RegisterCenter>b__7_2(Shards.Engine.ShardsContext)|20f8cca4884d127ae66caa1974b2b0c70e5642ec336cc13827a6ab8f507fb066
Shards.Content.ShardsHorizonSet+<>c::Boolean <RegisterDestinies>b__17_12(Shards.Engine.ShardsContext)|9999ae645f20f9cf3fa9f76e26f64121b270faf2f3bda801db75c10968a49d92
Shards.Content.ShardsHorizonSet+<>c::Boolean <RegisterDestinies>b__17_15(Shards.Engine.ShardsContext)|0f43c7e8cfe5c0244023c36ac3ff6c62f3270b3213a0bb2101d54d31862fa86d
Shards.Content.ShardsHorizonSet+<>c::Boolean <RegisterDestinies>b__17_16(Shards.Engine.ShardsContext)|f3b0fe44fd1d9787ea96ee05d6b0bd736b2e16e86ddac1aac00b756dea84ca9a
Shards.Content.ShardsHorizonSet+<>c::Boolean <RegisterDestinies>b__17_19(Shards.Engine.ShardsContext)|5d1168378a872417f9a3ca9bc8bb633569a7e2e17f2211a8865c7edc1e9ed96b
Shards.Content.ShardsHorizonSet+<>c::Boolean <RegisterDestinies>b__17_2(Shards.Engine.ShardsContext)|d73bdb9beb6edd6a7e49268566792dfb9b29540163181747a399e2a13d2e4bfe
Shards.Content.ShardsHorizonSet+<>c::Boolean <RegisterDestinies>b__17_20(Shards.Engine.ShardsContext)|c33385ce4162efb67b754ea0e00965daf088ae126a98ff317880391c609a8a41
Shards.Content.ShardsHorizonSet+<>c::Boolean <RegisterDestinies>b__17_21(Shards.Engine.ShardsContext)|ebbc1a115271957d781845677a91c9963ac375d69e78c779a8fde10de9316003
Shards.Content.ShardsHorizonSet+<>c::Boolean <RegisterDestinies>b__17_22(Shards.Engine.ShardsContext)|2f229ab910a868cf4bfd9d16b99cbbffd70ecdb5b48d024de52b91ea0f510b34
Shards.Content.ShardsHorizonSet+<>c::Boolean <RegisterDestinies>b__17_23(Shards.Engine.ShardsContext)|3bfbee64cb5e36fcc8cd7f2d622d7be01969907de044512b74f81c2e32e313ff
Shards.Content.ShardsHorizonSet+<>c::Boolean <RegisterDestinies>b__17_24(Shards.Engine.ShardsContext)|b502e639289a8fb30e1e669604e6ee4f658bdd9843eae49b534fb345d91b639e
Shards.Content.ShardsHorizonSet+<>c::Boolean <RegisterDestinies>b__17_25(Shards.Engine.ShardsContext)|b064e0d357b25bf4b9fe254ffc156fd317120ee7d8295d376d79bfbfdc95181a
Shards.Content.ShardsHorizonSet+<>c::Boolean <RegisterDestinies>b__17_26(Shards.Engine.ShardsContext)|60e846d076f13e6bd1ef3e6b09c56ce6520f2c3b63a25afe7379c76a1a0bef2e
Shards.Content.ShardsHorizonSet+<>c::Boolean <RegisterDestinies>b__17_27(Shards.Engine.ShardsContext)|72acc63e1986d048fde1c104573e54413ea7e19bf9877dce59e63f42d26e2b33
Shards.Content.ShardsHorizonSet+<>c::Boolean <RegisterDestinies>b__17_30(Shards.Engine.ShardsCard)|20be1a23347c54f84af768f8e4bf52995e8a3f0977a98e814d761ee7c97d455f
Shards.Content.ShardsHorizonSet+<>c::Boolean <RegisterDestinies>b__17_31(Shards.Engine.ShardsCard)|044566909abbab1ca67d278d8cbd7fe20728ca09be24ec7d8afab004809a1d4a
Shards.Content.ShardsHorizonSet+<>c::Boolean <RegisterDestinies>b__17_32(Shards.Engine.ShardsCard)|b410e2006750c9af67f609c4f3e2c7feef72fcf60a09afbdb1f537259cdac7aa
Shards.Content.ShardsHorizonSet+<>c::Boolean <RegisterDestinies>b__17_33(Shards.Engine.ShardsCard)|234452987667a74abab6c185cbfda731266884158a7cac6080703a03e5679b93
Shards.Content.ShardsHorizonSet+<>c::Boolean <RegisterDestinies>b__17_34(Shards.Engine.ShardsCard)|20d5b438aec899f39cfed8ae4c646841314f7e0970a8ff04241184e0b4288ea9
Shards.Content.ShardsHorizonSet+<>c::Boolean <RegisterDestinies>b__17_35(Shards.Engine.ShardsCard)|3dd35627a48d86a0075aa931019d598df8467c56f3693e2ef1e74972b193de17
Shards.Content.ShardsHorizonSet+<>c::Boolean <RegisterDestinies>b__17_7(Shards.Engine.ShardsContext)|8d1bf6a184d58b39db8fefe8ea4210b2094af854d3b28deecdadde6518edfafc
Shards.Content.ShardsHorizonSet+<>c::Boolean <RegisterDestinies>b__17_8(Shards.Engine.ShardsContext)|94a2c916b1b6d5b68ed47debfc9e319e901cdff7995566d0a1baeaf3d41e8409
Shards.Content.ShardsHorizonSet+<>c::Boolean <RegisterDestinies>b__17_9(Shards.Engine.ShardsContext)|b91c2ec071076fc14e43a05d3a8a6a8452fbf44ecce1a3b001e0fc39f6b13557
Shards.Content.ShardsHorizonSet+<>c::Boolean <RegisterIngeminex>b__14_0(Shards.Engine.ShardsCardDef)|8d714cf24345c1e4f1bd67887deed4d3a400177cde53e50b7ae9fc5030bf85d4
Shards.Content.ShardsHorizonSet+<>c::Boolean <ShardSeerFlow>b__12_0(Shards.Engine.ShardsCard)|4412e11f42849ca2d1b6e7b111c25a6dc1008e0aa77ff6a680f2ef4d11196c32
Shards.Content.ShardsHorizonSet+<>c::Int32 <RegisterCenter>b__7_1(Shards.Engine.ShardsContext)|cdee48258a83631727e905b84da20e511878b17b507fc546a5aaec793392a6db
Shards.Content.ShardsHorizonSet+<>c::Int32 <RegisterDestinies>b__17_13(Shards.Engine.ShardsContext)|a2c50047a58e45cbe4b6d7a9e8c705666f65406df5bb5111885a1ef7e3208ca7
Shards.Content.ShardsHorizonSet+<>c::Int32 <RegisterDestinies>b__17_28(Shards.Engine.ShardsPlayer, Shards.Engine.ShardsCard, Shards.Engine.ShardsCard)|7e156a3e27e82184f3980c3f2cf7d9e8e231d4be57487b8f7dde3ebb26820896
Shards.Content.ShardsHorizonSet+<>c::Int32 <RegisterDestinies>b__17_3(Shards.Engine.ShardsContext)|a7d1f9f1e442c1d557cae73c68115022427075a1c35117a9b8c17313283a7f21
Shards.Content.ShardsHorizonSet+<>c::Int32 <RegisterDestinies>b__17_6(Shards.Engine.ShardsContext)|7c92c762ae769d1399a036856475f444ea2c455f935fa6d1730c967e7a2b57b7
Shards.Content.ShardsHorizonSet+<>c::Shards.Engine.IShardsEffect <RegisterDestinies>b__17_29(Int32)|ad03e18849c4e865cb74403794ac34b584b5708d6fe10600da11528ae9b8a239
Shards.Content.ShardsHorizonSet+<>c::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] <RegisterDestinies>b__17_10(Shards.Engine.ShardsContext)|5417d2a4fe9a0e61ca3880e0da61fdbe3b6d13017b6e0f21e56e0c2fa52dd9f8
Shards.Content.ShardsHorizonSet+<>c::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] <RegisterDestinies>b__17_11(Shards.Engine.ShardsContext)|811e633d4a3c5ea307efd818ae9f00ea782aabbf70e3b1031f4a9b888a61c9c6
Shards.Content.ShardsHorizonSet+<>c::Void <RegisterCenter>b__7_0(Shards.Engine.ShardsContext)|25a56840006685e5f41ddccdb138f32984ca049fd5883e91651ae4e693b7204e
Shards.Content.ShardsHorizonSet+<>c__DisplayClass10_0::Boolean <RevealTopForChampion>b__0(Shards.Engine.ShardsCard)|9d53f0ee0ec811ed0c8cc58b9c7b9295b3438a2b3eb097155d000c806cb02104
Shards.Content.ShardsHorizonSet+<>c__DisplayClass11_0::Boolean <FabricatorFlow>b__0(Shards.Engine.ShardsCard)|1b8f79e2e8425bf24662f2568ffaa4ff608653333a820394a150d402b52a231a
Shards.Content.ShardsHorizonSet+<>c__DisplayClass11_0::Boolean <FabricatorFlow>b__1(Shards.Engine.ShardsCard)|901a3ad6e460102810d1e13f5a12b5a43ca0e04d3f67eca084bde1fdc22a4029
Shards.Content.ShardsHorizonSet+<>c__DisplayClass15_0::Boolean <CorruptionReward>b__1(Shards.Engine.ShardsCard)|075b5f034c4842a1f8d3789cf9afc7a68b399f5fde340f7995893a5e2c59f299
Shards.Content.ShardsHorizonSet+<>c__DisplayClass16_0::Boolean <BonusDestiny>b__0(Shards.Engine.ShardsCard)|d5d3c4a20b33c86caf4a7ca91db57feff2855e8c7edd169d4d8ed3e367e4e51e
Shards.Content.ShardsHorizonSet+<>c__DisplayClass18_0::Boolean <BloodForBloodFlow>b__1(Shards.Engine.ShardsCard)|b83b0ec0944544b750772aa174a8eef1b7c75699de5984f17c2d8ca131fd3119
Shards.Content.ShardsHorizonSet+<>c__DisplayClass20_0::Boolean <PowerStruggleFlow>b__0(Shards.Engine.ShardsCard)|c08a5add8c9fb1bb3f989a7045294d7c7c89a21eb67b1d6566e7b3cffe3c790f
Shards.Content.ShardsHorizonSet+<>c__DisplayClass22_0::Boolean <StolenFuturesFlow>b__0(Shards.Engine.ShardsCard)|5eee99fab90ef5bc0f9a4634e625e59eb979961280bf51b0b556ff5b6cf63fbc
Shards.Content.ShardsHorizonSet+<>c__DisplayClass7_0::Boolean <RegisterCenter>b__3(Shards.Engine.ShardsCard)|06e75c80b3e631e1ac899ef71e25270f7535cdd671bdbd4e196e66149d22435b
Shards.Content.ShardsHorizonSet+<>c__DisplayClass8_0::Boolean <ResetChampion>b__0(Shards.Engine.ShardsCard)|8fce2d8131bebc76273da862433bf997ecfd338c8820b66549727d9799451e6e
Shards.Content.ShardsHorizonSet+<>c__DisplayClass8_0::Boolean <ResetChampion>b__1(Shards.Engine.ShardsCard)|6a1fd4b10b9158ec6f66956bc60b07c905eabed549189314b75d521126e9d977
Shards.Content.ShardsHorizonSet+<BloodForBloodFlow>d__18::Boolean MoveNext()|84811d1b20e30683d258bf3d228f787c5d89b8982aac413fc1799b429336f9ad
Shards.Content.ShardsHorizonSet+<BonusDestiny>d__16::Boolean MoveNext()|5f2b8ad7e910a6e88218690efa43305a443b1cc9235e0100282903dfdeadfa26
Shards.Content.ShardsHorizonSet+<CorruptionReward>d__15::Boolean MoveNext()|281eb27e1a6c1a0b13d8884c47fd155a6fc3c38dbbd1bc8fd9c7fda0a09f7e74
Shards.Content.ShardsHorizonSet+<DeadlyRecruitsFlow>d__19::Boolean MoveNext()|b3d20a5639611095801a3584e6f03102b23d38cce89fa8fe7b0c6ba18bafaeb4
Shards.Content.ShardsHorizonSet+<FabricatorFlow>d__11::Boolean MoveNext()|625b7e0b492931fd6801c1113276018173aad725f16d13c4f021554d2e6462db
Shards.Content.ShardsHorizonSet+<FabricatorFlow>d__11::Void <>m__Finally1()|3d4d7482c59e3f49671b577d985c76b43e32b3bfbbca98efea5510c7792ca44b
Shards.Content.ShardsHorizonSet+<FabricatorFlow>d__11::Void System.IDisposable.Dispose()|45eeed044daa4ec7800cb5fb82d5702e5c555a55174cd3e80a69b20205468b89
Shards.Content.ShardsHorizonSet+<GatekeeperFlow>d__13::Boolean MoveNext()|a7b1e1e346b992f8a1979625f497f0ea4b7074c5dc440fdafec22de549903ce5
Shards.Content.ShardsHorizonSet+<PowerStruggleFlow>d__20::Boolean MoveNext()|ee647d7c96a82f7d95346a9a74307335c765f5426664f1ffff63537691f37826
Shards.Content.ShardsHorizonSet+<ResetChampion>d__8::Boolean MoveNext()|ffae194733264668812ae5401d164d909dfca3615357dfad2717e01e41fcb701
Shards.Content.ShardsHorizonSet+<RevealTopForChampion>d__10::Boolean MoveNext()|969671d40fc51bd5dfb92190178f2c4bebb818f024a402458fca6f0be9bfc927
Shards.Content.ShardsHorizonSet+<ShardDefiantFlow>d__21::Boolean MoveNext()|cabfdc848d266d371f65f635d0e7323a5caa0ca0f21592d9cded9b5c01930589
Shards.Content.ShardsHorizonSet+<ShardSeerFlow>d__12::Boolean MoveNext()|e72da4b11564c3e033371deb7041f62cfa8e1a6a57858042bffb2022f5171908
Shards.Content.ShardsHorizonSet+<StolenFuturesFlow>d__22::Boolean MoveNext()|017942d0e6bd38bffc0eeb0a4ff2a4bfc0cc8987555a27503aec7642efdbb28a
Shards.Content.ShardsHorizonSet::Int32 <RegisterDestinies>g__DistinctFactions|17_1(Shards.Engine.ShardsContext)|500efc01b1a3070ef01d4a86051289167f7c397fc236bc88ba9a1683915debab
Shards.Content.ShardsHorizonSet::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] BloodForBloodFlow(Shards.Engine.ShardsContext)|2e1ab957ae868ffc19436de88b8214270b790f8fb532f6fe3bacda4c0219e311
Shards.Content.ShardsHorizonSet::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] BonusDestiny(Shards.Engine.ShardsContext)|e957ecf9e674b4944e4c5d477d72025d1d2bdad720a126a95c25953d720028eb
Shards.Content.ShardsHorizonSet::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] CorruptionReward(Shards.Engine.ShardsContext)|39111db51fe89634568b4f1f9ec107953e87dec76944bf401e095247b82fb4c1
Shards.Content.ShardsHorizonSet::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] DeadlyRecruitsFlow(Shards.Engine.ShardsContext, Int32)|2b183f1e68e2332a1fedb708feab288eb5350e735e1515b82e51ab850008f1ee
Shards.Content.ShardsHorizonSet::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] FabricatorFlow(Shards.Engine.ShardsContext)|55143a482d09b1ca085f5aa1c5d2d9ab2fbf432139e6c9aea86bc471a26f8ae0
Shards.Content.ShardsHorizonSet::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] GatekeeperFlow(Shards.Engine.ShardsContext)|b3f6b8fb9ed66dd6550b689c89bb3d3f7999253da1372e99cd8cd040d4506ada
Shards.Content.ShardsHorizonSet::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] LegionCarrierFlow(Shards.Engine.ShardsContext)|c5113c042f9d5bc29c1e56a526642a291b051b1cd850bd1d9c40024c4d65551b
Shards.Content.ShardsHorizonSet::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] PowerStruggleFlow(Shards.Engine.ShardsContext)|97ef3c103ee57f9e0b1171899ae0104133cafb82f7c97bc9a20380ad848528e6
Shards.Content.ShardsHorizonSet::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] ResetChampion(Shards.Engine.ShardsContext)|6a44b36d03cba20bfaacced2e789f4e836c17db28cbfadf5eaf95ef06633758b
Shards.Content.ShardsHorizonSet::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] RevealTopForChampion(Shards.Engine.ShardsContext, Int32)|1cf735b857f43fd9e9958d7aa4f0f0b418d1c813eaf22c7354bc13148b363ddc
Shards.Content.ShardsHorizonSet::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] ShardDefiantFlow(Shards.Engine.ShardsContext)|33d1acfdebd5f46de4db31819672df549f1eca07c5623457170f685bad4b7106
Shards.Content.ShardsHorizonSet::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] ShardSeerFlow(Shards.Engine.ShardsContext)|0b577ad77f0500fb0660b50d4476f0162b6988bc3d6ada2c7f571bb8c7b9b73a
Shards.Content.ShardsHorizonSet::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] StolenFuturesFlow(Shards.Engine.ShardsContext)|65d4240bea99a0afaf17a8f5b5bb06f64e3cf2849c72f039ab3b79515d8cdc69
Shards.Content.ShardsRelicsSet+<>c::Boolean <RegisterCenter>b__6_3(Shards.Engine.ShardsState, Shards.Engine.ShardsPlayer, Shards.Engine.ShardsPlayer, Shards.Engine.ShardsCard)|de6315da4ed1dda456e648ac5210efa3ca606eac4f26f75d08ab40bd500bf5fa
Shards.Content.ShardsRelicsSet+<>c::Boolean <RegisterCenter>b__6_4(Shards.Engine.ShardsCardDef)|b6016f503ff3ce991b09dce408c8ade2f87efc77066af3e24af4313156c7b845
Shards.Content.ShardsRelicsSet+<>c::Boolean <RegisterRelics>b__8_6(Shards.Engine.ShardsCardDef)|10cc1a6f11fb5b3fa8961cef16b5e83e0e94c54db33c8c9e6c676caab9ef32e0
Shards.Content.ShardsRelicsSet+<>c::Boolean <RegisterRelics>b__8_7(Shards.Engine.ShardsCardDef)|f2161d3971917c57ab1e01d09dd79dfbf0f0a677d0bedbba5376a9e344cdacf1
Shards.Content.ShardsRelicsSet+<>c::Int32 <RegisterCenter>b__6_0(Shards.Engine.ShardsPlayer)|49cababd5ac7756dcd0d376b03d73b6d369eedc4255b26317c529a0c8a45635d
Shards.Content.ShardsRelicsSet+<>c::Int32 <RegisterCenter>b__6_1(Shards.Engine.ShardsContext)|0b8d081cb9c277d45d31c5ff25826750748aea61d9092226afb0cdb2e8559424
Shards.Content.ShardsRelicsSet+<>c::Int32 <RegisterCenter>b__6_2(Shards.Engine.ShardsPlayer, Shards.Engine.ShardsCard, Shards.Engine.ShardsCard)|daffe86614cd97e77a1084a7b81fc276fc4c90c87201008da80e7a1c38dcc37a
Shards.Content.ShardsRelicsSet+<>c::Int32 <RegisterRelics>b__8_0(Shards.Engine.ShardsPlayer)|acc1b7144e7c07d13926431d45b0c8815f312a366f9812b78f3950d19db3b1a3
Shards.Content.ShardsRelicsSet+<>c::Int32 <RegisterRelics>b__8_1(Shards.Engine.ShardsPlayer)|5701e79eebd7d5bf4e5ab285461d43d443e587093a1f08386129b147120238a2
Shards.Content.ShardsRelicsSet+<>c::Int32 <RegisterRelics>b__8_2(Shards.Engine.ShardsContext)|b22535661efeca6fed28815c20aa25fdb05b0d927846d4591ab4f310dd8a1d8c
Shards.Content.ShardsRelicsSet+<>c::Int32 <RegisterRelics>b__8_3(Shards.Engine.ShardsContext)|1bdd9f9edae78eefb5cf356f30c35c49989de3e730d0b5c2f2fe8e49d938fd1a
Shards.Content.ShardsRelicsSet+<>c::Void <RegisterCenter>b__6_5(Shards.Engine.ShardsContext)|074f6075b0536cf26ba47e4577bd0b3af03ff2dea9bea5d6d9b1ece14f717e2b
Shards.Content.ShardsRelicsSet+<>c::Void <RegisterRelics>b__8_4(Shards.Engine.ShardsContext)|9f11524fd2c8dca0e11c35c3608e9404a51f34874afcea530b9686dee351e829
Shards.Content.ShardsRelicsSet+<>c::Void <RegisterRelics>b__8_5(Shards.Engine.ShardsContext)|7c9c7c6cc19927197d5316945106db830c02c48c2183d525dbc90fc0f4dd5da4
Shards.Content.ShardsRelicsSet::Boolean HighestMastery(Shards.Engine.ShardsContext)|24fa4a6a4d73c50f812974f6bdbb39fa2e0e1c22b772fde1020e517dec2c7a8e
Shards.Content.ShardsShadowSet+<>c::Boolean <DashFlow>b__4_0(Shards.Engine.ShardsCard)|9406de7b4387b92056d819e8613eb67816f1c6c125e429f23b3a4391cff1c822
Shards.Content.ShardsShadowSet+<>c__DisplayClass4_0::Boolean <DashFlow>b__1(Shards.Engine.ShardsCard)|6f2639d8074b8c67a511d7ef4e82feb59e964f31cad59383546e1e594acbf844
Shards.Content.ShardsShadowSet+<>c__DisplayClass6_0::Boolean <WarpquartzBanish>b__0(Shards.Engine.ShardsCard)|092e362a730f14ec113a52e16f4ecbb2f01f492d9442e6081a720268b42e8782
Shards.Content.ShardsShadowSet+<DashFlow>d__4::Boolean MoveNext()|fc89f776994161e2b062a9dc4cff768aa32367fc5fcbfefe728807abad86afcc
Shards.Content.ShardsShadowSet+<ExtraTurn>d__5::Boolean MoveNext()|201a1734ae32686163c499845898905f6b92b0d5266704e520b912d06c677a9f
Shards.Content.ShardsShadowSet+<WarpquartzBanish>d__6::Boolean MoveNext()|9e11abad1714462c92e11ce552dfe2698bac96b9e543f139c59cab5a9409ce90
Shards.Content.ShardsShadowSet+<WarpquartzBanish>d__6::Void <>m__Finally1()|6f19bb153a1efa9b3ec34dd3b4030e48ef22d8af98bb7b98a3cc5c168f753747
Shards.Content.ShardsShadowSet+<WarpquartzBanish>d__6::Void <>m__Finally2()|5e1f4a86b8e0015da5522bdaaf47b592a221c558b7d44a81a1f9195b358e11f5
Shards.Content.ShardsShadowSet+<WarpquartzBanish>d__6::Void System.IDisposable.Dispose()|f92468fc22ee19edfc23ef429fb9fc64e0a90e5029ae5973549d7a65479bd038
Shards.Content.ShardsShadowSet::Boolean HighestMastery(Shards.Engine.ShardsContext)|182731ffc545c08f7f25f46cfbcb6985420a7e58cb3daf735f3065923fa5b19e
Shards.Content.ShardsShadowSet::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] DashFlow(Shards.Engine.ShardsContext)|9738be91b9683fd655f70e4261de9c8b0e13de423ee2d7c363fb374ba2840116
Shards.Content.ShardsShadowSet::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] ExtraTurn(Shards.Engine.ShardsContext)|54b10443009a3adc6f6a67fa6d4529de2ab73819b9a0212fb2242e9d727cb74e
Shards.Content.ShardsShadowSet::System.Collections.Generic.IEnumerable`1[Shards.Engine.ShardsStep] WarpquartzBanish(Shards.Engine.ShardsContext)|e3b1f6b49698b391750dd760be985a8e943a1198adfd1ab8e9d2b9b9635ff19b
Shards.Engine.If+<>c::Boolean <Echo>b__8_0(Shards.Engine.ShardsContext)|b2391abc8e4a8fe14575711bffe31860052ca9a2d3c729e6a400d81a6684e6dc
Shards.Engine.If+<>c::Boolean <FullHealth>b__10_0(Shards.Engine.ShardsContext)|dbb98c4a3d3aea812f9c19789c9ebdc734b922ae45da314a1d9d4b89de6c3f4f
Shards.Engine.If+<>c::Boolean <Inspire>b__7_0(Shards.Engine.ShardsContext)|e4fc3a6ca6d0f9c54d9cf88ec7da6c9c6612c34310b4016db1499db57580aec0
Shards.Engine.If+<>c__DisplayClass8_0::Boolean <Echo>b__1(Shards.Engine.ShardsCard)|9a33252a6b356ce7e309e83187ad9d9f85da5d5811f33e497bc3425296e8779c
Shards.Engine.If+<>c__DisplayClass9_0::Boolean <Character>b__0(Shards.Engine.ShardsContext)|ecd54ccf499c6067882cc9900a1099b9b6f433d083fae2e03b0515c6c32404f7";
        private static Dictionary<string,string> Approved => ApprovedManifest.Split('\n',StringSplitOptions.RemoveEmptyEntries)
            .Where(x=>x.Contains('|')).ToDictionary(x=>x[..x.LastIndexOf('|')],x=>x[(x.LastIndexOf('|')+1)..].Trim(),StringComparer.Ordinal);

        internal static object Descriptor => new {
            schema=Schema, width=Width, labels=Labels(), normalization="Resources/losses/per-unit resources/end-turn draws /10; cost limits /13; mastery thresholds /30; count_limit /10; operation capabilities and gates 0/1 per occurrence. Callback literal bins/branch counts /10. Captured numeric summaries /30. Pooling sums without clipping. Static hook shield /50, cost /13, defense /10.",
            hookFixtures=new {ownerMastery=10,ownerHeroes=new[]{"decima","decima","decima","decima","tetra","tetra","tetra","tetra"},
                homodeusChampionCounts=new[]{0,1,3,4,5,6,8,10},attackerMastery=new[]{0,5,9,10,15,19,20,30},
                source="described card",target="public Homodeus champion",shieldMastery=new[]{0,5,9,10,15,19,20,30},damageAmounts=new[]{0,1,4,5,9,10}},
            coverage=new {cards=Cards, callbackObjects=Callbacks, effectTypes=Types, explicitCustomRecipes=RecipesUsed.OrderBy(x=>x).ToArray(), methodChecksums=Checksums},
            limitations=new[]{"Phase-separated pooled summary loses exact ordering and branch-to-operation binding.",
                "Callback operation recipes are source-audited; generic dependency/literal summaries are not a full symbolic program.",
                "Changes to arbitrary callback code require review and manifest refresh; captured numeric fields are read at descriptor construction.",
                "No name, card identifier, rules text, method identity, checksum, or hidden game state enters the feature vector."}
        };

        internal static float[][] BuildMatrix() => BuildMatrixCore(true);
        internal static float[][] AuditMatrix() => BuildMatrixCore(false);
        private static float[][] BuildMatrixCore(bool verify)
        {
            if(Features.Length!=96) throw new InvalidOperationException("Effect feature count");
            Checksums.Clear(); Types.Clear(); RecipesUsed.Clear(); Cards=Callbacks=0;
            var result=Enumerable.Range(0,193).Select(_=>new float[Width]).ToArray();
            EnforceManifest=verify;
            try {
                for(int i=0;i<Encoder.CardIds.Length;i++) {result[i+1]=BuildCard(ShardsCardDatabase.Get(Encoder.CardIds[i]));Cards++;}
                if(verify) VerifyManifest(Approved);
                return result;
            } finally {EnforceManifest=false;}
        }
        internal static void VerifyManifest(Dictionary<string,string> approved)
        {
            foreach(var pair in Checksums)
                if(!approved.TryGetValue(pair.Key,out var hash)||hash!=pair.Value)
                    throw new InvalidOperationException("Unreviewed effect callback: "+pair.Key+"|"+pair.Value);
        }
        internal static Dictionary<string,string> CurrentManifest() => new(Checksums,StringComparer.Ordinal);
        internal static string ManifestText() => string.Join("\n",Checksums.Select(p=>p.Key+"|"+p.Value));
        internal static float[] BuildCard(ShardsCardDef d)
        {
            var v=new float[Width];
            v[0]=d.Cost/13f;v[1]=d.Defense/10f;v[2]=d.Shield/10f;v[3]=d.ExhaustGemCost/10f;
            v[4+(int)d.Type]=1;v[16+(int)d.Faction]=1;
            int hero=Array.IndexOf(ShardsEngine.DraftableCharacters,d.Character);if(hero>=0)v[24+hero]=1;
            bool[] flags={d.IsChampion,d.IsMonster,d.ShieldInPlay,d.Taunt,d.ReturnsFromDiscardOnChampionPlay,d.RedirectChampionRecruitsToDeckTop,d.RecruitsToHand,d.CountsAsEveryFaction,d.CannotBeRerolled,d.CannotBeFastPlayed,d.ShieldsProtectChampions,d.ImmuneToIngeminex};
            for(int i=0;i<flags.Length;i++)v[32+i]=flags[i]?1:0;
            v[44]=d.KeepFastPlaysAtMastery>=0?1:0;v[45]=Math.Max(0,d.KeepFastPlaysAtMastery)/30f;
            v[46]=d.DoublesExhaustsAtMastery>=0?1:0;v[47]=Math.Max(0,d.DoublesExhaustsAtMastery)/30f;
            int keep=Array.IndexOf(ShardsEngine.DraftableCharacters,d.KeepFastPlaysCharacter);if(keep>=0)v[48+keep]=1;
            if(d.ReturnFromDiscardOnFactionPlay!=ShardsFaction.None)v[54+(int)d.ReturnFromDiscardOnFactionPlay]=1;
            var effects=new[]{d.PlayEffect,d.ExhaustEffect,d.RewardEffect,d.MonsterAttackEffect};
            for(int phase=0;phase<4;phase++) Walk(effects[phase],v,64+96*phase,false);
            Delegate[] hooks={d.CanBeAttacked,d.DefenseAura,d.CostModifier,d.OnDamageDealt,d.DynamicShield,d.DiscardPassiveShield};
            for(int i=0;i<hooks.Length;i++) if(hooks[i]!=null){v[448+i]=1;Callback(hooks[i],new float[96],0,"hook");}
            // Public counterfactual mastery probes: evaluate only pure shield hooks.
            int[] mastery={0,5,9,10,15,19,20,30};
            for(int i=0;i<mastery.Length;i++) {
                var p=new ShardsPlayer{Mastery=mastery[i]};
                if(d.DynamicShield!=null)v[454+i]=d.DynamicShield(p)/50f;
                if(d.DiscardPassiveShield!=null)v[462+i]=d.DiscardPassiveShield(p)/50f;
            }
            // Eight fixed PUBLIC counterfactual fixture contexts, no actual game.
            // Vary faction ownership, champion count, hero and attacker mastery.
            for(int i=0;i<8;i++) {
                var owner=new ShardsPlayer{Mastery=10,CharacterId=i<4?"decima":"tetra"};
                int n=new[]{0,1,3,4,5,6,8,10}[i];
                for(int j=0;j<n;j++)owner.Champions.Add(new ShardsCard{DefId="general_decurion"});
                var source=new ShardsCard{DefId=d.Id};var target=new ShardsCard{DefId="general_decurion"};
                if(d.CostModifier!=null)v[470+i]=d.CostModifier(owner)/13f;
                if(d.DefenseAura!=null)v[478+i]=d.DefenseAura(owner,source,target)/10f;
                var attacker=new ShardsPlayer{Mastery=new[]{0,5,9,10,15,19,20,30}[i]};
                if(d.CanBeAttacked!=null)v[486+i]=d.CanBeAttacked(new ShardsState(),attacker,owner,source)?1:0;
            }
            if(d.OnDamageDealt!=null) {
                int[] damages={0,1,4,5,9,10};
                for(int i=0;i<damages.Length;i++) {
                    var effect=d.OnDamageDealt(damages[i]);var scratch=new float[96];Walk(effect,scratch,0,false);
                    v[494+i*3]=effect!=null?1:0;v[495+i*3]=scratch[15];v[496+i*3]=scratch[4]+scratch[9];
                }
            }
            if(v.Any(x=>float.IsNaN(x)||float.IsInfinity(x)))throw new InvalidOperationException("Nonfinite descriptor");
            return v;
        }
        private static T Get<T>(object o,string field) => (T)(o.GetType().GetField(field,Fields)?.GetValue(o) ?? throw new InvalidOperationException("Unknown effect field "+o.GetType()+"."+field));
        private static void Add(float[] v,int offset,string key,float amount=1)
        {int i=Array.IndexOf(Features,key);if(i<0)throw new InvalidOperationException(key);v[offset+i]+=amount;}
        private static void Gain(float[] v,int offset,int gems,int power,int mastery,int health,int draw,bool conditional,string prefix=null)
        {
            int start=prefix=="per_unit"?10:conditional?5:0;
            int[] amounts={gems,power,mastery,health,draw};for(int i=0;i<5;i++)v[offset+start+i]+=amounts[i]/10f;
        }
        private static void Faction(float[]v,int o,ShardsFaction faction)
        {
            string name=faction.ToString().ToLowerInvariant();if(Array.IndexOf(Features,"faction_"+name)>=0)Add(v,o,"faction_"+name);
        }
        private static void Walk(IShardsEffect e,float[]v,int o,bool conditional)
        {
            if(e==null||e is ShardsNullEffect)return;
            string type=e.GetType().Name;Types[type]=Types.GetValueOrDefault(type)+1;
            switch(e) {
                case Gain g: Gain(v,o,g.Gems,g.Power,g.Mastery,g.Health,g.Draw,conditional);break;
                case ShardsComposite x: Add(v,o,"composite_parts",x.Parts.Count/10f);foreach(var part in x.Parts)Walk(part,v,o,conditional);break;
                case AtMastery x: Add(v,o,"mastery_gate");Add(v,o,"mastery_threshold_sum",x.Threshold/30f);Walk(x.Inner,v,o,true);break;
                case BestByMastery x: Add(v,o,"best_mastery_tiers",x.Tiers.Count/10f);foreach(var tier in x.Tiers){Add(v,o,"mastery_threshold_sum",tier.threshold/30f);Walk(tier.effect,v,o,true);}break;
                case FactionTrigger x: Add(v,o,"faction_gate");Add(v,o,"faction_required_sum",x.Required/10f);Faction(v,o,x.Faction);Walk(x.Inner,v,o,true);break;
                case AllegianceEffect x: Add(v,o,"allegiance_gate");Add(v,o,"faction_required_sum",x.Required/10f);Faction(v,o,x.Faction);Walk(x.Inner,v,o,true);break;
                case Unify x: Add(v,o,"unify_gate");Faction(v,o,Get<ShardsFaction>(x,"_faction"));Walk(x.Inner,v,o,true);break;
                case Dominion x: Add(v,o,"dominion_gate");Add(v,o,"condition_distinct_factions",3/10f);Walk(x.Inner,v,o,true);break;
                case If x: Callback(Get<Delegate>(x,"_condition"),v,o,"condition");Walk(x.Inner,v,o,true);break;
                case PerCount x: var a=x.PerUnit;Gain(v,o,a.gems,a.power,a.mastery,a.health,a.draw,true,"per_unit");Callback(Get<Delegate>(x,"_counter"),v,o,"counter");break;
                case Custom x: Callback(Get<Delegate>(x,"_body"),v,o,"custom");break;
                case Do x: Callback(Get<Delegate>(x,"_body"),v,o,"immediate");break;
                case OpponentLosesMastery x: Add(v,o,"enemy_mastery_loss",x.Amount/10f);break;
                case BanishUpTo x: Add(v,o,"banish");Add(v,o,"count_limit",x.Count/10f);Add(v,o,"choice");if(Get<bool>(x,"_optional"))Add(v,o,"optional");break;
                case ReturnFromDiscard x: Add(v,o,"return_cards");Add(v,o,"to_hand");Add(v,o,"condition_discard");Callback(Get<Delegate>(x,"_filter"),v,o,"filter");if(Get<bool>(x,"_all"))Add(v,o,"all_targets");if(Get<bool>(x,"_optional"))Add(v,o,"optional");break;
                case DestroyEnemyChampions x: Add(v,o,"destroy_champion");if(Get<bool>(x,"_all"))Add(v,o,"all_targets");break;
                case WarpUpTo x: Add(v,o,"free_fast_play");Add(v,o,"cost_limit",x.MaxCost/13f);Add(v,o,"optional");break;
                case RecruitFromRow x: Add(v,o,"free_recruit");Add(v,o,"cost_limit",x.MaxCost/13f);if(x.ToHand)Add(v,o,"to_hand");break;
                case CopyPlayedEffect x: Add(v,o,"copy_effect");Add(v,o,"count_limit",x.Copies/10f);Callback(Get<Delegate>(x,"_filter"),v,o,"filter");break;
                case AllPlayersLoseHealth x: Add(v,o,"enemy_health_loss",x.Amount/10f);if(x.IncludeController)Add(v,o,"self_health_loss",x.Amount/10f);break;
                case AllPlayersLoseMastery x: Add(v,o,"enemy_mastery_loss",x.Amount/10f);Add(v,o,"all_targets");break;
                case AllPlayersDiscard x: Add(v,o,"discard",Get<int>(x,"_count")/10f);Add(v,o,"all_targets");break;
                case AllPlayersDestroyBiggestChampion _: Add(v,o,"destroy_champion");Add(v,o,"all_targets");Add(v,o,"condition_card_cost");break;
                case Scry x: Add(v,o,"scry_center");Add(v,o,"count_limit",x.Count/10f);break;
                case ReorderCenterTop x: Add(v,o,"reorder_center");Add(v,o,"count_limit",x.Count/10f);break;
                case DiscountNextReroll _: Add(v,o,"reroll_discount",.1f);break;
                case VolosAbilityChoice _: Add(v,o,"choice");for(int mode=0;mode<4;mode++)Walk(VolosAbilityChoice.Effect(mode),v,o,true);break;
                default: throw new InvalidOperationException("Uncovered effect type "+e.GetType().FullName);
            }
        }

        // Explicit reviewed Custom operation coverage. This supplements actual IL
        // dependency/quantity extraction; names only dispatch, never become inputs.
        private static readonly Dictionary<string,string[]> CustomOperations = new(StringComparer.Ordinal) {
            ["DecurionCopy"]=new[]{"copy_effect","condition_played","faction_homodeus"},
            ["DashFlow"]=new[]{"return_cards","condition_discard","to_deck_top","faction_aion","optional"},
            ["DashDuel"]=new[]{"return_cards","condition_discard","to_deck_top","faction_aion","optional","mastery_gate"},
            ["ExtraTurn"]=new[]{"extra_turn"},["WarpquartzBanish"]=new[]{"banish","copy_effect","condition_hand","condition_discard","optional"},
            ["WarpquartzDuel"]=new[]{"banish","copy_effect","condition_hand","condition_discard","optional","per_unit_gems","per_unit_power"},
            ["ResetChampion"]=new[]{"reset_champion","optional"},["LegionCarrierFlow"]=new[]{"reveal","to_hand","condition_champions"},
            ["RevealTopForChampion"]=new[]{"reveal","to_hand","condition_champions"},["FabricatorFlow"]=new[]{"reveal","copy_effect"},
            ["FabricatorDuel"]=new[]{"reveal","copy_effect","mastery_gate"},["ShardSeerFlow"]=new[]{"reveal","conditional_mastery","condition_hand"},
            ["GatekeeperFlow"]=new[]{"reveal","to_hand","self_health_loss","enemy_health_loss","condition_card_cost","mastery_gate"},
            ["CorruptionReward"]=new[]{"grant_relic","to_hand"},["BonusDestiny"]=new[]{"grant_destiny"},
            ["BloodForBloodFlow"]=new[]{"banish","condition_played","optional"},["DeadlyRecruitsFlow"]=new[]{"free_fast_play","keep_fast_play","optional"},
            ["DeadlyRecruitsDuel"]=new[]{"free_fast_play","keep_fast_play","optional","choice"},["PowerStruggleFlow"]=new[]{"destroy_champion","conditional_power","optional"},
            ["ShardDefiantFlow"]=new[]{"reveal","free_recruit","banish","choice","faction_aion"},["StolenFuturesFlow"]=new[]{"banish","grant_destiny"},
            ["ReactorChoice"]=new[]{"choice","conditional_gems","banish"},["RemoveFromShop"]=new[]{"reroll_discount","optional"},
            ["RiposteBonus"]=new[]{"reveal","condition_shield","conditional_power"},["WorldPiercerDuel"]=new[]{"return_cards","search_deck","condition_discard","to_hand","mastery_gate"},
            ["GrimTutor"]=new[]{"search_deck","to_hand","self_health_loss"},["DoomGatePlay"]=new[]{"monster_operation"},
            ["DoomGateDestroy"]=new[]{"monster_operation","choice"},["Longshot"]=new[]{"reveal","free_fast_play","mastery_gate","cost_limit"},
            ["DestroyOpponent"]=new[]{"enemy_health_loss"},["BleakCommunion"]=new[]{"self_health_loss","enemy_health_loss","draw","condition_discard","faction_wraethe"}
        };
        private static readonly HashSet<string> QuantityFeatures = new(new[]{"gems","power","mastery","health","draw","conditional_gems","conditional_power","conditional_mastery","conditional_health","conditional_draw","per_unit_gems","per_unit_power","self_health_loss","enemy_health_loss"});
        private static readonly Dictionary<string,Dictionary<string,float>> CustomQuantities = new() {
            ["ShardSeerFlow"]=new(){{"conditional_mastery",.2f}},
            ["PowerStruggleFlow"]=new(){{"conditional_power",.5f}},
            ["ReactorChoice"]=new(){{"conditional_gems",.5f}}, // alternative amounts 2+3, pooled
            ["RiposteBonus"]=new(){{"conditional_power",.3f}},
            ["BleakCommunion"]=new(){{"draw",.2f},{"self_health_loss",.4f},{"enemy_health_loss",.4f}},
            ["GrimTutor"]=new(){{"self_health_loss",.3f}},
            ["WarpquartzDuel"]=new(){{"per_unit_gems",.3f},{"per_unit_power",.3f}},
            ["DestroyOpponent"]=new(){{"enemy_health_loss",100000f}},
            ["GatekeeperFlow"]=new(){{"self_health_loss",.1f},{"enemy_health_loss",.1f}}, // per revealed card cost, paired with condition_card_cost
            ["Longshot"]=new(){{"count_limit",.2f},{"cost_limit",8/13f},{"mastery_threshold_sum",.5f}},
            
            ["DoomGatePlay"]=new(){{"count_limit",2.5f}},
            ["StolenFuturesFlow"]=new(){{"count_limit",.2f}},
            ["WorldPiercerDuel"]=new(){{"count_limit",.2f},{"mastery_threshold_sum",20/30f}},
            ["WarpquartzBanish"]=new(){{"count_limit",.3f}}
        };
        private static readonly Dictionary<string,string> Dependency = new(StringComparer.Ordinal) {
            ["Champions"]="condition_champions",["get_IsChampion"]="condition_champions",["CountChampions"]="condition_champions",
            ["Discard"]="condition_discard",["CountDiscard"]="condition_discard",["PlayedThisTurn"]="condition_played",["FactionPlays"]="condition_played",["FactionAllyPlays"]="condition_played",
            ["Hand"]="condition_hand",["Health"]="condition_health",["MaxHealth"]="condition_health",["Mastery"]="condition_mastery",["Cost"]="condition_card_cost",
            ["DistinctFactionsPlayed"]="condition_distinct_factions",["DistinctFactions"]="condition_distinct_factions",["Shield"]="condition_shield",["ActiveMonsters"]="condition_monsters",
            ["CharacterId"]="condition_character",["HighestMastery"]="condition_highest_mastery",["IgnoreShieldsThisTurn"]="ignore_shields",["HealthToPowerThisTurn"]="health_to_power",
            ["OverflowHealthToPowerThisTurn"]="health_to_power",["HealingDoubledThisTurn"]="double_healing",["ShieldsDoubledUntilNextTurn"]="double_shields",
            ["NextHomodeusChampionsIntoPlay"]="next_champion_into_play",["NextChampionsIntoPlay"]="next_champion_into_play",
            ["NextRecruitsToHand"]="next_recruit_to_hand"
        };
        private static void Callback(Delegate callback,float[]v,int o,string role)
        {
            Callbacks++;Add(v,o,"callback_count");
            var methods=Graph(callback.Method);var found=new HashSet<string>();var constants=new List<double>();var semantic=new HashSet<string>();
            foreach(var method in methods) {
                var bytes=Canonical(method);string key=MethodKey(method);string hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bytes))).ToLowerInvariant();
                Checksums[key]=hash;
                if(EnforceManifest && (!Approved.TryGetValue(key,out var approved)||approved!=hash))
                    throw new InvalidOperationException("Unreviewed effect callback: "+key+"|"+hash);
                if(CustomOperations.ContainsKey(method.Name))found.Add(method.Name);
                if(Dependency.TryGetValue(method.Name,out var feature))semantic.Add(feature);
                var instructions=Instructions(method).ToArray();
                for(int position=0;position<instructions.Length;position++) {
                    var instruction=instructions[position];
                    // Literal helper arguments are actual compiled quantities.
                    // This automatically distinguishes Deadly Recruits' cost tiers
                    // and Legion Carrier's reveal depth without card-ID dispatch.
                    if(position>0 && instructions[position-1].Number.HasValue && instruction.Member is MethodBase helper) {
                        float amount=(float)instructions[position-1].Number.Value;
                        if(helper.Name=="DeadlyRecruitsFlow"||helper.Name=="DeadlyRecruitsDuel")Add(v,o,"cost_limit",amount/13f);
                        if(helper.Name=="RevealTopForChampion")Add(v,o,"count_limit",amount/10f);
                    }
                    if(instruction.Op==OpCodes.Stfld && instruction.Member?.Name=="BonusDrawsOnBigHit" && position>0 && instructions[position-1].Number.HasValue)
                        Add(v,o,"end_turn_bonus_draw",(float)instructions[position-1].Number.Value/10f);
                    if(instruction.Member!=null && Dependency.TryGetValue(instruction.Member.Name,out feature))semantic.Add(feature);
                    if(instruction.Number.HasValue)constants.Add(instruction.Number.Value);
                    if(instruction.Op.FlowControl==FlowControl.Branch||instruction.Op.FlowControl==FlowControl.Cond_Branch)Add(v,o,"callback_branch_count",.1f);
                }
            }
            if(role=="custom"&&found.Count==0)throw new InvalidOperationException("Custom has no explicit recipe: "+MethodKey(callback.Method));
            foreach(string recipe in found) {RecipesUsed.Add(recipe);foreach(string key in CustomOperations[recipe])semantic.Add(key);}
            // Resource quantities in explicit recipes are normalized amounts, never
            // confused with operation-presence flags. Their reviewed numeric
            // constants are guarded by the callback method manifest.
            foreach(string key in semantic)if(!QuantityFeatures.Contains(key))Add(v,o,key);
            foreach(string recipe in found)if(CustomQuantities.TryGetValue(recipe,out var quantities))
                foreach(var quantity in quantities)Add(v,o,quantity.Key,quantity.Value);
            if(role=="immediate"&&methods.Any(m=>Instructions(m).Any(i=>i.Member?.Name=="GainPower")))
                Add(v,o,"per_unit_power",.1f); // audited Ko Syn Wu relic doubles current power

            foreach(double n in constants.Distinct()) {int index=Array.IndexOf(Literals,Math.Abs(n));if(index>=0)v[o+80+index]+=.1f;}
            // Captured quantities are real runtime values, not stale source text.
            if(callback.Target!=null)foreach(var field in callback.Target.GetType().GetFields(Fields)) {
                object value=field.GetValue(callback.Target);
                if(value is int n){Add(v,o,"captured_numeric_sum",n/30f);Add(v,o,"captured_numeric_abs_sum",Math.Abs(n)/30f);}
                else if(value is float f){Add(v,o,"captured_numeric_sum",f/30f);Add(v,o,"captured_numeric_abs_sum",Math.Abs(f)/30f);}
                else if(value is ShardsFaction faction)Faction(v,o,faction);
                else if(value is string text){int hero=Array.IndexOf(ShardsEngine.DraftableCharacters,text);if(hero>=0)Add(v,o,"condition_character");}
            }
        }

        private sealed class Instruction {internal OpCode Op;internal MemberInfo Member;internal double? Number;internal string Operand;}
        private static IEnumerable<Instruction> Instructions(MethodBase method)
        {
            byte[] il=method.GetMethodBody()?.GetILAsByteArray();if(il==null)yield break;
            for(int p=0;p<il.Length;) {
                short code=il[p++];if(code==0xfe)code=(short)(0xfe00|il[p++]);var op=Opcodes[code];var item=new Instruction{Op=op,Operand=""};
                int size=op.OperandType switch {
                    OperandType.InlineNone=>0,OperandType.ShortInlineBrTarget=>1,OperandType.ShortInlineI=>1,OperandType.ShortInlineVar=>1,
                    OperandType.InlineVar=>2,OperandType.InlineI8=>8,OperandType.InlineR=>8,
                    OperandType.InlineSwitch=>4+4*BitConverter.ToInt32(il,p),_=>4};
                if(op.OperandType==OperandType.InlineMethod||op.OperandType==OperandType.InlineField||op.OperandType==OperandType.InlineType||op.OperandType==OperandType.InlineTok) {
                    int token=BitConverter.ToInt32(il,p);
                    try{item.Member=method.Module.ResolveMember(token,method.DeclaringType?.GetGenericArguments(),method.IsGenericMethod?method.GetGenericArguments():null);item.Operand=MemberKey(item.Member);}
                    catch(ArgumentException){throw new InvalidOperationException("Cannot resolve audited callback member "+method);}
                } else if(op.OperandType==OperandType.InlineString)item.Operand=method.Module.ResolveString(BitConverter.ToInt32(il,p));
                else if(size>0)item.Operand=Convert.ToHexString(il.AsSpan(p,size));
                if(op==OpCodes.Ldc_I4)item.Number=BitConverter.ToInt32(il,p);
                else if(op==OpCodes.Ldc_I4_S)item.Number=(sbyte)il[p];
                else if(op==OpCodes.Ldc_I4_M1)item.Number=-1;
                else if(op.Value>=OpCodes.Ldc_I4_0.Value&&op.Value<=OpCodes.Ldc_I4_8.Value)item.Number=op.Value-OpCodes.Ldc_I4_0.Value;
                else if(op==OpCodes.Ldc_R4)item.Number=BitConverter.ToSingle(il,p);
                else if(op==OpCodes.Ldc_R8)item.Number=BitConverter.ToDouble(il,p);
                p+=size;yield return item;
            }
        }
        private static string MemberKey(MemberInfo m)=>m==null?"":m.DeclaringType?.FullName+"::"+m;
        private static string MethodKey(MethodBase m)=>MemberKey(m);
        private static bool Owned(MethodBase m)=>m.DeclaringType?.Namespace=="Shards.Content" || (m.DeclaringType?.FullName?.StartsWith("Shards.Engine.If",StringComparison.Ordinal)??false);
        private static IEnumerable<MethodBase> Graph(MethodInfo first)
        {
            var seen=new HashSet<MethodBase>();var queue=new Queue<MethodBase>();queue.Enqueue(first);
            while(queue.Count>0) {
                var current=queue.Dequeue();if(!seen.Add(current))continue;yield return current;
                var iterator=current.GetCustomAttribute<IteratorStateMachineAttribute>();
                if(iterator!=null)foreach(var method in iterator.StateMachineType.GetMethods(Fields))if(method.Name=="MoveNext")queue.Enqueue(method);
                foreach(var instruction in Instructions(current))if(instruction.Member is MethodBase method&&Owned(method)&&method.Name!=".ctor")queue.Enqueue(method);
            }
        }
        private static string Canonical(MethodBase method)=>MethodKey(method)+"\n"+string.Join("\n",Instructions(method).Select(i=>i.Op.Name+" "+i.Operand));
        private static string[] Labels()
        {
            var labels=Enumerable.Range(0,Width).Select(i=>"reserved_zero_"+i).ToArray();
            labels[0]="cost_div13";labels[1]="defense_div10";labels[2]="shield_div10";labels[3]="exhaust_gems_div10";
            foreach(ShardsCardType x in Enum.GetValues(typeof(ShardsCardType)))labels[4+(int)x]="type_"+x;
            foreach(ShardsFaction x in Enum.GetValues(typeof(ShardsFaction)))labels[16+(int)x]="faction_"+x;
            for(int i=0;i<5;i++){labels[24+i]="owner_hero_"+ShardsEngine.DraftableCharacters[i];labels[48+i]="keep_fastplay_hero_"+ShardsEngine.DraftableCharacters[i];}
            string[] flags={"champion","monster","shield_in_play","taunt","return_on_champion_play","champion_recruit_to_top","recruits_to_hand","all_factions","cannot_reroll","must_buy_normally","shields_protect_champions","monster_immunity"};for(int i=0;i<flags.Length;i++)labels[32+i]=flags[i];
            labels[44]="keep_fastplay_mastery_enabled";labels[45]="keep_fastplay_mastery_div30";labels[46]="double_exhaust_mastery_enabled";labels[47]="double_exhaust_mastery_div30";
            foreach(ShardsFaction x in Enum.GetValues(typeof(ShardsFaction)))if(x!=ShardsFaction.None)labels[54+(int)x]="return_on_faction_play_"+x;
            for(int phase=0;phase<4;phase++)for(int j=0;j<96;j++)labels[64+96*phase+j]=PhaseNames[phase]+"."+Features[j];
            string[] hooks={"attack_veto","defense_aura","cost_modifier","damage_trigger","dynamic_shield","discard_shield"};for(int i=0;i<hooks.Length;i++)labels[448+i]="hook_"+hooks[i];
            int[] mastery={0,5,9,10,15,19,20,30};for(int i=0;i<8;i++){
                labels[454+i]="shield_at_mastery_"+mastery[i];labels[462+i]="discard_shield_at_mastery_"+mastery[i];
                labels[470+i]="cost_modifier_public_fixture_"+i;labels[478+i]="defense_aura_public_fixture_"+i;labels[486+i]="attack_allowed_public_fixture_"+i;
            }
            int[] damage={0,1,4,5,9,10};for(int i=0;i<6;i++){labels[494+i*3]="trigger_on_damage_"+damage[i];labels[495+i*3]="banish_on_damage_"+damage[i];labels[496+i*3]="draw_on_damage_"+damage[i];}
            return labels;
        }
    }
}
