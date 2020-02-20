﻿using Akka.Actor;
using Akka.Configuration;
using Lyra.Core.API;
using Lyra.Core.Blocks;
using Lyra.Core.Utils;
using Lyra.Shared;
using Microsoft.Extensions.Logging;
using Neo;
using Neo.IO.Actors;
using Neo.Network.P2P;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Neo.Network.P2P.LocalNode;
using Settings = Neo.Settings;

namespace Lyra.Core.Decentralize
{
    public class TransStats
    {
        public long ms { get; set; }
        public BlockTypes trans { get; set; }
    }
    // when out of sync, we adjust useed, continue to save blocks, and told blockchain to do sync.
    public enum ConsensusWorkingMode { Normal, OutofSyncWaiting }
    /// <summary>
    /// about seed generation: the seed0 seed will generate UIndex whild sending authorization message.
    /// </summary>
    public class ConsensusService : ReceiveActor
    {
        public class Startup { }
        public class Consolidate { }
        public class AskForBillboard { }
        public class AskForStats { }
        public class AskForDbStats { }
        public class BlockChainSynced { }
        public class NodeInquiry { }
        public class Authorized { public bool IsSuccess { get; set; } }
        private readonly IActorRef _localNode;

        ILogger _log;
        Orphanage _orphange;

        ConcurrentDictionary<string, ConsensusWorker> _activeConsensus;
        ConcurrentDictionary<string, ConsensusWorker> _cleanedConsensus;
        private static BillBoard _board = new BillBoard();
        private List<TransStats> _stats;

        private long _UIndexSeed = -1;
        private object _seedLocker = new object();

        public long USeed => _UIndexSeed;
        public long GenSeed()
        {
            lock(_seedLocker)
            {
                return _UIndexSeed++;
            }
        }

        public static bool IsThisNodeSeed0 => NodeService.Instance.PosWallet.AccountId == ProtocolSettings.Default.StandbyValidators[0];
        public bool IsMessageFromSeed0(SourceSignedMessage msg)
        {
            return msg.From == ProtocolSettings.Default.StandbyValidators[0];
        }
        public ConsensusWorkingMode Mode { get; private set; }
        public static BillBoard Board { get => _board; }
        public List<TransStats> Stats { get => _stats; }

        // authorizer snapshot
        public static HashSet<string> AuthorizerShapshot { get; private set; }

        public ConsensusService(IActorRef localNode)
        {
            _localNode = localNode;
            _log = new SimpleLogger("ConsensusService").Logger;

            _activeConsensus = new ConcurrentDictionary<string, ConsensusWorker>();
            _cleanedConsensus = new ConcurrentDictionary<string, ConsensusWorker>();
            _stats = new List<TransStats>();

            while (BlockChain.Singleton == null)
                Task.Delay(100).Wait();

            Mode = ConsensusWorkingMode.OutofSyncWaiting;

            _orphange = new Orphanage(
                    async (state) => { var worker = GetWorker(state.InputMsg.Block.Hash); worker.Create(state); },
                    async (msg1) => {
                        await OnNextConsensusMessageAsync(msg1);
                    },
                    async (msg2s) => {
                        foreach (var msg2 in msg2s)
                        {
                            var worker2 = GetWorker(msg2.BlockHash);
                            if (worker2 != null)
                                await worker2.OnPrepareAsync(msg2);
                        }
                    },
                    async (msg3s) => {
                        foreach (var msg3 in msg3s)
                        {
                            var worker3 = GetWorker(msg3.BlockHash);
                            if (worker3 != null)
                                await worker3.OnCommitAsync(msg3);
                        }
                    }
                );

            ReceiveAsync<Startup>(async state =>
            {
                await DeclareConsensusNodeAsync();
            });

            ReceiveAsync<BlockChain.BlockAdded>(async (ba) =>
            {
                await _orphange.BlockAddedAsync(ba.hash);
            });

            Receive<Consolidate>((_) =>
            {
                //_log.LogInformation("Doing Consolidate");
                //OnNodeActive(NodeService.Instance.PosWallet.AccountId);     // update billboard

                //if (Mode == ConsensusWorkingMode.Normal && _board != null && _board.CanDoConsensus)
                //{
                //    Task.Run(async () =>
                //    {
                //        await GenerateConsolidateBlockAsync();
                //    });
                //}
            });

            Receive<BillBoard>((bb) =>
            {
                //_board = bb;
                //foreach (var node in _board.AllNodes.Values)
                //    {
                //        if(node.AccountID != NodeService.Instance.PosWallet.AccountId)
                //            _pBFTNet.AddPosNode(node);
                //    }                    
            });

            Receive<AskForBillboard>((_) => { Sender.Tell(_board); });
            Receive<AskForStats>((_) => Sender.Tell(_stats));
            Receive<AskForDbStats>((_) => Sender.Tell(PrintProfileInfo()));

            ReceiveAsync<SignedMessageRelay>(async relayMsg =>
            {
                if (relayMsg == null || relayMsg.signedMessage == null)
                    return;

                if (relayMsg.signedMessage.Version == LyraGlobal.ProtocolVersion)
                    try
                    {
                        await OnNextConsensusMessageAsync(relayMsg.signedMessage);
                    }
                    catch(Exception ex)
                    {
                        _log.LogCritical("OnNextConsensusMessageAsync!!! " + ex.ToString());
                    }
                else
                    _log.LogWarning("Protocol Version Mismatch. Do nothing.");
            });

            ReceiveAsync<BlockChainSynced>(async _ =>
            {
                Mode = ConsensusWorkingMode.Normal;
                _UIndexSeed = (await BlockChain.Singleton.FindLatestBlockAsync()).UIndex + 1;

                _log.LogInformation($"The USeed is {USeed}");

                int waitCount = 60;
                while (LocalNode.Singleton.RemoteNodes.Count < 1 && waitCount > 0)
                {
                    _log.LogInformation("Not connected to Lyra Network. Delay sending... ");
                    await Task.Delay(1000);
                    waitCount--;
                }

                await DeclareConsensusNodeAsync();
            });

            ReceiveAsync<AuthState>(async state =>
            {
                //TODO: check  || _context.Board == null || !_context.Board.CanDoConsensus
                if(state.InputMsg.Block is TransactionBlock)
                {
                    var acctId = (state.InputMsg.Block as TransactionBlock).AccountID;
                    if (FindActiveBlock(acctId, state.InputMsg.Block.Index))
                    {
                        _log.LogCritical($"Double spent detected for {acctId}, index {state.InputMsg.Block.Index}");
                        return;
                    }
                }

                if (await AddOrphanAsync(state))
                    return;

                var worker = GetWorker(state.InputMsg.Block.Hash);
                worker.Create(state);
            });

            Receive<NodeInquiry>((_) => {
                var inq = new ChatMsg("", ChatMessageType.NodeStatusInquiry);
                Send2P2pNetwork(inq);
                _log.LogInformation("Inquiry for node status.");
            });

            //Task.Run(async () =>
            //{
            //    await HeartBeatAsync();
            //    int count = 0;
            //    while (true)
            //    {
            //        if (Mode == ConsensusWorkingMode.Normal)
            //        {
            //            await GenerateConsolidateBlockAsync();
            //        }

            //        //await Task.Delay(5000).ConfigureAwait(false);
            //        //count++;

            //        //if(count > 6)
            //        //{
            //        //    await HeartBeatAsync();
            //        //    count = 0;
            //        //}                    
            //    }
            //});
        }

        public Task<bool> AddOrphanAsync(AuthState state)
        {
            return _orphange.TryAddOneAsync(state);
        }

        private string PrintProfileInfo()
        {
            // debug: measure time
            // debug only
            var dat = StopWatcher.Data;

            var sbLog = new StringBuilder();

            var q = dat.Select(g => new
             {
                 name = g.Key,
                 times = g.Value.Count(),
                 totalTime = g.Value.Sum(t => t.MS),
                 avgTime = g.Value.Sum(t => t.MS) / g.Value.Count()
            })
             .OrderByDescending(b => b.totalTime);
            foreach (var d in q)
            {
                sbLog.AppendLine($"Total time: {d.totalTime} times: {d.times} avg: {d.avgTime} ms. Method Name: {d.name}  ");
            }

            var info = sbLog.ToString();

            _log.LogInformation("\n------------------------\n" + sbLog.ToString() + "\n------------------------\\n");

            StopWatcher.Reset();
            return info;
        }

        public virtual void Send2P2pNetwork(SourceSignedMessage item)
        {
            item.Sign(NodeService.Instance.PosWallet.PrivateKey, item.From);
            //item.Hash = "a";
            //item.Signature = "a";

            while (LocalNode.Singleton.ConnectedCount < 1)
            {
                _log.LogInformation("p2p network not connected. delay sending message...");
                Task.Delay(1000).Wait();
            }
            _localNode.Tell(item);
        }

        private async Task DeclareConsensusNodeAsync()
        {
            // declare to the network
            PosNode me = new PosNode(NodeService.Instance.PosWallet.AccountId);
            me.IP = $"{await GetPublicIPAddress.PublicIPAddressAsync(Settings.Default.LyraNode.Lyra.NetworkId != "devnet")}";

            var msg = new ChatMsg(JsonConvert.SerializeObject(me), ChatMessageType.NodeUp);
            _board.Add(me);
            Send2P2pNetwork(msg);
        }

        private async Task HeartBeatAsync()
        {
            OnNodeActive(NodeService.Instance.PosWallet.AccountId);     // update billboard

            // declare to the network
            var msg = new ChatMsg
            {
                From = NodeService.Instance.PosWallet.AccountId,
                MsgType = ChatMessageType.HeartBeat,
                Text = "I'm live"
            };

            Send2P2pNetwork(msg);

            if (IsThisNodeSeed0)
            {
                await BroadCastBillBoardAsync();
            }
        }

        public bool FindActiveBlock(string accountId, long index)
        {
            return _activeConsensus.Values.Where(s => s.State.InputMsg.Block is TransactionBlock)
                .Select(t => t.State.InputMsg.Block as TransactionBlock)
                .Any(a => a.AccountID == accountId && a.Index == index);
        }

        private async Task GenerateConsolidateBlockAsync()
        {
            // expire partial transaction.
            // "patch" the exmpty UIndex
            // collec fees and do redistribute
            var lastCons = await BlockChain.Singleton.GetSyncBlockAsync();
            ConsolidationBlock currentCons = null;
            try
            {
                Monitor.Enter(_seedLocker);

                // first clean cleaned states
                var cleaned = _cleanedConsensus.Values.ToArray();
                for (int i = 0; i < cleaned.Length; i++)
                {
                    var state = cleaned[i].State;
                    if (DateTime.Now - state.Created > TimeSpan.FromMinutes(1)) // 2 mins
                    {
                        var finalResult = state.Consensus;
                        if (finalResult == ConsensusResult.Uncertain)
                            _log.LogWarning($"Permanent remove timeouted Uncertain block: {state.InputMsg.Block.Hash}");
                        _cleanedConsensus.TryRemove(state.InputMsg.Block.Hash, out _);
                    }
                }

                var states = _activeConsensus.Values.ToArray();
                for (int i = 0; i < states.Length; i++)
                {
                    var state = states[i].State;    // TODO: check null
                    if (state != null && DateTime.Now - state.Created > TimeSpan.FromSeconds(20)) // consensus timeout
                    {
                        var finalResult = state.Consensus;
                        if(finalResult == ConsensusResult.Uncertain)
                            _log.LogWarning($"temporary remove timeouted Uncertain block: {state.InputMsg.Block.Hash}");

                        _activeConsensus.TryRemove(state.InputMsg.Block.Hash, out _);
                        state.Done?.Set();

                        _cleanedConsensus.TryAdd(state.InputMsg.Block.Hash, states[i]);

                        //if (finalResult == true)
                        //    continue;

                        //// replace the failed block with nulltrans
                        //var myAuthResult = state.OutputMsgs.FirstOrDefault(a => a.From == NodeService.Instance.PosWallet.AccountId);
                        //if (myAuthResult == null)
                        //{
                        //    // fatal error. should not happen
                        //    _log.LogError("No auth result from seed0. should not happen.");
                        //    continue;
                        //}

                        //var ndx = myAuthResult.BlockUIndex;
                        //if (ndx == 0)    // not got yet
                        //    continue;

                        //// check if the block is orphaned success block
                        //var existingBlock = BlockChain.Singleton.GetBlockByUIndex(ndx);
                        //if (existingBlock != null)
                        //{
                        //    _log.LogInformation($"in GenerateConsolidateBlock: orphaned message for {ndx} detected.");
                        //    continue;
                        //}
                        
                        // no need for this. just leave it as hole
                        //var nb = new NullTransactionBlock
                        //{
                        //    UIndex = ndx,
                        //    FailedBlockHash = myAuthResult.BlockHash,
                        //    NetworkId = lastCons.NetworkId,
                        //    ShardId = lastCons.ShardId,
                        //    ServiceHash = lastCons.ServiceHash,
                        //    AccountID = NodeService.Instance.PosWallet.AccountId,
                        //    PreviousConsolidateHash = lastCons.Hash
                        //};
                        //nb.InitializeBlock(null, NodeService.Instance.PosWallet.PrivateKey,
                        //    lastCons.NetworkId, lastCons.ShardId,
                        //    NodeService.Instance.PosWallet.AccountId);
                        //nb.UHash = SignableObject.CalculateHash($"{nb.UIndex}|{nb.Index}|{nb.Hash}");

                        //SendServiceBlock(nb);
                    }
                }

                //// if necessary, insert a new ConsolidateBlock
                //if (USeed - lastCons.UIndex > 1024 || DateTime.Now - lastCons.TimeStamp > TimeSpan.FromMinutes(10))
                //{
                //    var authGenesis = BlockChain.Singleton.GetLastServiceBlock();
                //    currentCons = new ConsolidationBlock
                //    {
                //        UIndex = USeed++,
                //        NetworkId = authGenesis.NetworkId,
                //        ShardId = authGenesis.ShardId,
                //        ServiceHash = authGenesis.Hash,
                //        SvcAccountID = NodeService.Instance.PosWallet.AccountId
                //    };
                //}
            }
            catch (Exception ex)
            {
                _log.LogError("Error In GenerateConsolidateBlock: " + ex.Message);
            }
            finally
            {
                Monitor.Exit(_seedLocker);
            }

            //if (currentCons != null)
            //{
            //    var mt = new MerkleTree();
            //    for (var ndx = lastCons.UIndex + 1; ndx < currentCons.UIndex; ndx++)      // TODO: handling "losing" block here
            //    {
            //        var block = BlockChain.Singleton.GetBlockByUIndex(ndx);
            //        if (block == null)
            //        {
            //            _log.LogError("GenerateConsolidateBlock Fatal Error!!! should not happend.");
            //            Task.Delay(100000000).Wait();
            //        }
            //        var mhash = MerkleHash.Create(block.UHash);
            //        mt.AppendLeaf(mhash);
            //    }

            //    currentCons.MerkelTreeHash = mt.BuildTree().ToString();
            //    currentCons.InitializeBlock(lastCons, NodeService.Instance.PosWallet.PrivateKey,
            //        currentCons.NetworkId, currentCons.ShardId,
            //        NodeService.Instance.PosWallet.AccountId);

            //    currentCons.UHash = SignableObject.CalculateHash($"{currentCons.UIndex}|{currentCons.Index}|{currentCons.Hash}");

            //    SendServiceBlock(currentCons);

            //    // use merkle tree to consolidate all previous blocks, from lastCons.UIndex to xx[consBlock.UIndex -1]xx may lost the newest block
            //    // if the block is old enough ( > 2 mins ), it should be replaced by NullTransactionBlock.
            //    // in fact we should reserve consolidate block number and wait 2min to do consolidating
            //    // all null block's previous block is the last consolidate block, it's index is counted from 1 related to previous block
            //}
        }

        //private AuthState SendServiceBlock(TransactionBlock svcBlock)
        //{
        //    AuthorizingMsg msg = new AuthorizingMsg
        //    {
        //        From = NodeService.Instance.PosWallet.AccountId,
        //        Block = svcBlock,
        //        MsgType = ChatMessageType.AuthorizerPrePrepare
        //    };

        //    var state = CreateAuthringState(msg);
        //    var localAuthResult = LocalAuthorizingAsync(msg);
        //    state.AddAuthResult(localAuthResult);

        //    if (!localAuthResult.IsSuccess)
        //    {
        //        _log.LogError($"Fatal Error: Consolidate block local authorization failed: {localAuthResult.Result}");
        //    }
        //    else
        //    {
        //        Send2P2pNetwork(msg);
        //        Send2P2pNetwork(localAuthResult);
        //    }

        //    return state;
        //}

        public static Props Props(IActorRef localNode)
        {
            return Akka.Actor.Props.Create(() => new ConsensusService(localNode)).WithMailbox("consensus-service-mailbox");
        }

        private ConsensusWorker GetWorker(string hash)
        {
            if (_cleanedConsensus.ContainsKey(hash))        // > 2min outdated.
            {
                _log.LogWarning($"GetWorker: no worker for expired hash: {hash.Shorten()}");
                return null;
            }

            if(_activeConsensus.ContainsKey(hash))
                return _activeConsensus[hash];
            else
            {
                var worker = new ConsensusWorker(this);
                if (_activeConsensus.TryAdd(hash, worker))
                    return worker;
                else
                    return _activeConsensus[hash];
            }
        }

        async Task OnNextConsensusMessageAsync(SourceSignedMessage item)
        {
            //_log.LogInformation($"Consensus: OnNextConsensusMessageAsync Called: {item.MsgType} From: {item.From.Shorten()}");

            if(null == AuthorizerShapshot && !(item is ChatMsg))
            {
                _log.LogWarning("AuthorizerShapshot is null.");
                return;
            }

            if(item.MsgType != ChatMessageType.NodeUp)
                OnNodeActive(item.From);

            switch (item)
            {
                case AuthorizingMsg msg1:
                    if(msg1.Block is TransactionBlock)
                    {
                        var acctId = (msg1.Block as TransactionBlock).AccountID;
                        if (FindActiveBlock(acctId, msg1.Block.Index))
                        {
                            _log.LogCritical($"Double spent detected for {acctId}, index {msg1.Block.Index}");
                            break;
                        }
                    }

                    if (msg1.Block is ServiceGenesisBlock && !IsMessageFromSeed0(item))
                    {
                        _log.LogError($"fake genesis block from node {item.From}");
                        return;
                    }                        

                    var worker = GetWorker(msg1.Block.Hash);
                    if (worker != null)
                        await worker.OnPrePrepareAsync(msg1);
                    else
                        _log.LogError($"No worker1 for {msg1.Block.Hash}");
                    break;
                case AuthorizedMsg msg2:
                    //_log.LogInformation($"Consensus: OnNextConsensusMessageAsync 3 {item.MsgType}");

                    if (!AuthorizerShapshot.Contains(msg2.From))
                        return;

                    var worker2 = GetWorker(msg2.BlockHash);
                    if (worker2 != null)
                        await worker2.OnPrepareAsync(msg2);
                    else
                        _log.LogError($"No worker2 for {msg2.BlockHash}");
                    //_log.LogInformation($"Consensus: OnNextConsensusMessageAsync 4 {item.MsgType}");
                    break;
                case AuthorizerCommitMsg msg3:
                    if (!AuthorizerShapshot.Contains(msg3.From))
                        return;

                    var worker3 = GetWorker(msg3.BlockHash);
                    if (worker3 != null)
                        await worker3.OnCommitAsync(msg3);
                    else
                        _log.LogError($"No worker3 for {msg3.BlockHash}");
                    break;
                case ChatMsg chat:
                    await OnRecvChatMsg(chat);
                    break;
                default:
                    // log msg unknown
                    break;
            }
        }

        private async Task OnRecvChatMsg(ChatMsg chat)
        {
            switch(chat.MsgType)
            {
                case ChatMessageType.HeartBeat:
                    OnHeartBeat(chat);
                    break;
                case ChatMessageType.NodeUp:
                    await OnNodeUpAsync(chat);
                    break;
                case ChatMessageType.BillBoardBroadcast:
                    OnBillBoardBroadcast(chat);
                    break;
                case ChatMessageType.BlockConsolidation:
                    await OnBlockConsolicationAsync(chat);
                    break;
                case ChatMessageType.NodeStatusInquiry:
                    var status = await BlockChain.Singleton.GetNodeStatusAsync();
                    var resp = new ChatMsg(JsonConvert.SerializeObject(status), ChatMessageType.NodeStatusReply);
                    Send2P2pNetwork(resp);
                    break;
                case ChatMessageType.NodeStatusReply:
                    var statusReply = JsonConvert.DeserializeObject<NodeStatus>(chat.Text);
                    LyraSystem.Singleton.TheBlockchain.Tell(statusReply);
                    break;
            }
        }

        private async Task OnBlockConsolicationAsync(ChatMsg msg)
        {
            if (!IsThisNodeSeed0)
            {
                var block = JsonConvert.DeserializeObject<ConsolidationBlock>(msg.Text);
                try
                {
                    await BlockChain.Singleton.AddBlockAsync(block);
                    _log.LogInformation($"Receive and store ConsolidateBlock of UIndex: {block.UIndex}");
                }
                catch(Exception e)
                {
                    _log.LogInformation($"OnBlockConsolication UIndex: {block.UIndex} Failed: {e.Message}");
                }                
            }
        }

        private void OnBillBoardBroadcast(ChatMsg msg)
        {
            if (!IsThisNodeSeed0 && IsMessageFromSeed0(msg)) // only accept bbb from seeds
            {
                _board = JsonConvert.DeserializeObject<BillBoard>(msg.Text);
                AuthorizerShapshot = _board.PrimaryAuthorizers.ToHashSet();

                // no me?
                if(!_board.AllNodes.ContainsKey(NodeService.Instance.PosWallet.AccountId))
                {
                    Task.Run(async () => { 
                        await DeclareConsensusNodeAsync();
                    });
                }

                _log.LogInformation("BillBoard updated!");
            }
        }

        private async Task RefreshPosBalanceAsync()
        {
            foreach(var node in _board.AllNodes.Values.ToList())
            {
                // lookup balance
                var block = await BlockChain.Singleton.FindLatestBlockAsync(node.AccountID) as TransactionBlock;
                if (block != null && block.Balances != null && block.Balances.ContainsKey(LyraGlobal.LYRATICKERCODE))
                {
                    node.Balance = block.Balances[LyraGlobal.LYRATICKERCODE];
                }
                else
                {
                    node.Balance = 0;
                }
            }
        }

        private async Task BroadCastBillBoardAsync()
        {
            if(_board != null)
            {
                await RefreshPosBalanceAsync();
                OnNodeActive(NodeService.Instance.PosWallet.AccountId);
                var deadNodes = _board.AllNodes.Values.Where(a => DateTime.Now - a.LastStaking > TimeSpan.FromHours(2)).ToList();
                foreach(var node in deadNodes)
                {
                    _board.AllNodes.Remove(node.AccountID);
                }
                _board.SnapShot();
                AuthorizerShapshot = _board.PrimaryAuthorizers.ToHashSet();
                var msg = new ChatMsg(JsonConvert.SerializeObject(_board), ChatMessageType.BillBoardBroadcast);
                Send2P2pNetwork(msg);
            }
        }

        public void OnNodeActive(string nodeAccountId)
        {
            if (_board != null)
                _board.Refresh(nodeAccountId);
        }

        private void OnHeartBeat(ChatMsg chat)
        {
            if (_board != null)
            {
                _board.Refresh(chat.From);
            }
        }

        private async Task OnNodeUpAsync(ChatMsg chat)
        {
            if (_board == null)
                return;

            var node = chat.Text.UnJson<PosNode>();
            //if (Utilities.IsPrivate(node.IP) && Settings.Default.LyraNode.Lyra.NetworkId != "devnet")
            //    return;

            _ = _board.Add(node);

            if (IsMessageFromSeed0(chat))    // seed0 up
            {
                await DeclareConsensusNodeAsync();      // we need resend node up message to codinator.
            }

            if (IsThisNodeSeed0)
            {
                // broadcast billboard
                await BroadCastBillBoardAsync();
            }

            if (_board.AllNodes.ContainsKey(node.AccountID) && _board.AllNodes[node.AccountID].IP == node.IP)
                return;

            if (node.Balance < LyraGlobal.MinimalAuthorizerBalance)
            {
                _log.LogInformation("Node {0} has not enough balance: {1}.", node.AccountID, node.Balance);
            }
        }
    }

    internal class ConsensusServiceMailbox : PriorityMailbox
    {
        public ConsensusServiceMailbox(Akka.Actor.Settings settings, Config config)
            : base(settings, config)
        {
        }

        internal protected override bool IsHighPriority(object message)
        {
            switch (message)
            {
                //case ConsensusPayload _:
                //case ConsensusService.SetViewNumber _:
                //case ConsensusService.Timer _:
                //case Blockchain.PersistCompleted _:
                //                    return true;
                default:
                    return false;
            }
        }
    }
}
