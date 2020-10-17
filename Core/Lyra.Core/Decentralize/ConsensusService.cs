﻿using Akka.Actor;
using Akka.Configuration;
using Clifton.Blockchain;
using Lyra.Core.API;
using Lyra.Core.Blocks;
using Lyra.Core.Utils;
using Lyra.Shared;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
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
using Stateless;
using Org.BouncyCastle.Utilities.Net;
using System.Net;
using Shared;
using Lyra.Data.API;
using Lyra.Data.Crypto;

namespace Lyra.Core.Decentralize
{
    /// <summary>
    /// about seed generation: the seed0 seed will generate UIndex whild sending authorization message.
    /// </summary>
    public partial class ConsensusService : ReceiveActor
    {
        public class Startup { }
        public class AskForBillboard { }
        public class AskForState { }
        public class AskForStats { }
        public class AskForDbStats { }
        public class QueryBlockchainStatus { }

        public class Authorized { public bool IsSuccess { get; set; } }
        private readonly IActorRef _localNode;
        private readonly IActorRef _blockchain;

        private readonly StateMachine<BlockChainState, BlockChainTrigger> _stateMachine;
        private readonly StateMachine<BlockChainState, BlockChainTrigger>.TriggerWithParameters<long> _engageTriggerStart;
        private readonly StateMachine<BlockChainState, BlockChainTrigger>.TriggerWithParameters<string> _engageTriggerConsolidateFailed;
        public BlockChainState CurrentState => _stateMachine.State;

        readonly ILogger _log;

        ConcurrentDictionary<string, DateTime> _criticalMsgCache;

        ConcurrentDictionary<string, ConsensusWorker> _activeConsensus;
        List<Vote> _lastVotes;
        private BillBoard _board;
        private List<TransStats> _stats;
        private System.Net.IPAddress _myIpAddress;

        // status inquiry
        private List<NodeStatus> _nodeStatus;

        public bool IsThisNodeLeader => _sys.PosWallet.AccountId == Board.CurrentLeader;
        public bool IsThisNodeSeed => ProtocolSettings.Default.StandbyValidators.Contains(_sys.PosWallet.AccountId);

        public BillBoard Board { get => _board; }
        public List<TransStats> Stats { get => _stats; }

        private ViewChangeHandler _viewChangeHandler;

        // how many suscess consensus did since started.
        private int _successBlockCount;

        // authorizer snapshot
        private DagSystem _sys;
        public DagSystem GetDagSystem() => _sys;
        public ConsensusService(DagSystem sys, IActorRef localNode, IActorRef blockchain)
        {
            _sys = sys;
            _localNode = localNode;
            _blockchain = blockchain;
            _log = new SimpleLogger("ConsensusService").Logger;
            _successBlockCount = 0;

            _criticalMsgCache = new ConcurrentDictionary<string, DateTime>();
            _activeConsensus = new ConcurrentDictionary<string, ConsensusWorker>();
            _stats = new List<TransStats>();
            _nodeStatus = new List<NodeStatus>();

            _board = new BillBoard();

            _viewChangeHandler = new ViewChangeHandler(_sys, this, (sender, viewId, leader, votes, voters) => {
                _log.LogInformation($"New leader selected: {leader} with votes {votes}");
                _board.LeaderCandidate = leader;
                _board.LeaderCandidateVotes = votes;

                if(leader == _sys.PosWallet.AccountId)
                {
                    // its me!
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(1000);     // some nodes may not got this in time
                        await CreateNewViewAsNewLeaderAsync();
                    });
                }

                _ = Task.Run(async () =>
                {
                    await Task.Delay(10000);

                    _board.LeaderCandidate = null;

                    var sb = await _sys.Storage.GetLastServiceBlockAsync();
                    if(sb.Height < viewId)
                    {
                        _log.LogCritical($"The new leader {leader.Shorten()} failed to generate service block. redo election.");
                        // the new leader failed.

                        if (CurrentState == BlockChainState.Almighty)
                        {
                            // redo view change
                            _viewChangeHandler.BeginChangeView(false);
                        }
                    }
                });
                    //_viewChangeHandler.Reset(); // wait for svc block generated
            });

            Receive<Startup>(state =>
            {
                _stateMachine.Fire(BlockChainTrigger.LocalNodeStartup);
            });

            ReceiveAsync<QueryBlockchainStatus>(async _ =>
            {
                var status = await GetNodeStatusAsync();
                Sender.Tell(status);
            });

            Receive<AskForState>((_) => Sender.Tell(_stateMachine.State));
            Receive<AskForBillboard>((_) => { Sender.Tell(_board); });
            Receive<AskForStats>((_) => Sender.Tell(_stats));
            Receive<AskForDbStats>((_) => Sender.Tell(PrintProfileInfo()));

            ReceiveAsync<SignedMessageRelay>(async relayMsg =>
            {
                try
                {
                    var signedMsg = relayMsg.signedMessage;

                    //_log.LogInformation($"ReceiveAsync SignedMessageRelay from {signedMsg.From.Shorten()} Hash {(signedMsg as BlockConsensusMessage)?.BlockHash}");
                    
                    if (signedMsg.TimeStamp < DateTime.UtcNow.AddSeconds(3) &&
                        signedMsg.TimeStamp > DateTime.UtcNow.AddSeconds(-18) &&                        
                        signedMsg.VerifySignature(signedMsg.From))
                    {
                        await CriticalRelayAsync(signedMsg, async (msg) =>
                        {
                            await OnNextConsensusMessageAsync(msg);
                        });
                    }
                    else
                    {
                        //_log.LogWarning($"Receive Relay illegal type {signedMsg.MsgType} Delayed {(DateTime.UtcNow - signedMsg.TimeStamp).TotalSeconds}s Verify: {signedMsg.VerifySignature(signedMsg.From)} From: {signedMsg.From.Shorten()}");
                    }
                }
                catch (Exception ex)
                {
                    _log.LogCritical("Error Receive Relay!!! " + ex.ToString());
                }
            });

            ReceiveAsync<AuthState>(async state =>
            {
                // not accepting new transaction from API
                // service block generate as usual.
                if (_viewChangeHandler.IsViewChanging)
                    return;

                _log.LogInformation($"State told.");
                if (_stateMachine.State != BlockChainState.Almighty && _stateMachine.State != BlockChainState.Engaging && _stateMachine.State != BlockChainState.Genesis)
                {
                    state.Done?.Set();
                    return;
                }

                if(_viewChangeHandler.TimeStarted != DateTime.MinValue)
                {
                    // view changing in progress. no block accepted
                    state.Done?.Set();
                    return;
                }

                //TODO: check  || _context.Board == null || !_context.Board.CanDoConsensus
                if (state.InputMsg.Block is TransactionBlock)
                {
                    var acctId = (state.InputMsg.Block as TransactionBlock).AccountID;
                    if (FindActiveBlock(acctId, state.InputMsg.Block.Height))
                    {
                        _log.LogCritical($"Double spent detected for {acctId}, index {state.InputMsg.Block.Height}");
                        return;
                    }
                    state.SetView(Board.PrimaryAuthorizers);
                }
                else if(state.InputMsg.Block is ServiceBlock)
                {
                    state.SetView(Board.AllVoters);
                }

                await SubmitToConsensusAsync(state);
            });

            Receive<BlockChain.BlockAdded>(nb =>
            {
                //if (nb.NewBlock is ServiceBlock lastSvcBlk)
                //{
                //    _board.PrimaryAuthorizers = lastSvcBlk.Authorizers.Select(a => a.AccountID).ToArray();
                //    if (!string.IsNullOrEmpty(lastSvcBlk.Leader))
                //        _board.CurrentLeader = lastSvcBlk.Leader;
                //    _board.AllVoters = _board.PrimaryAuthorizers.ToList();

                //    IsViewChanging = false;
                //    _viewChangeHandler.Reset();
                //}
            });

            Receive<Idle>(o => { });

            ReceiveAny((o) => { _log.LogWarning($"consensus svc receive unknown msg: {o.GetType().Name}"); });

            _stateMachine = new StateMachine<BlockChainState, BlockChainTrigger>(BlockChainState.NULL);
            _engageTriggerStart = _stateMachine.SetTriggerParameters<long>(BlockChainTrigger.ConsensusNodesInitSynced);
            _engageTriggerConsolidateFailed = _stateMachine.SetTriggerParameters<string>(BlockChainTrigger.LocalNodeOutOfSync);
            CreateStateMachine();

            var timr = new System.Timers.Timer(200);
            timr.Elapsed += async (s, o) =>
            {
                try
                {
                    if (_viewChangeHandler.CheckTimeout())
                    {
                        _log.LogInformation($"View Change with Id {_viewChangeHandler.ViewId} begin {_viewChangeHandler.TimeStarted} Ends: {DateTime.Now} used: {DateTime.Now - _viewChangeHandler.TimeStarted}");
                        _viewChangeHandler.Reset();
                    }

                    foreach (var worker in _activeConsensus.Values.ToArray())
                    {
                        if (worker.CheckTimeout())
                        {
                            var result = worker.State?.CommitConsensus;

                            if (worker.State != null)
                            {
                                worker.State.Done?.Set();
                                worker.State.Close();
                            }

                            _activeConsensus.TryRemove(worker.Hash, out _);

                            if (result.HasValue && result.Value == ConsensusResult.Uncertain && CurrentState == BlockChainState.Almighty)
                            {
                                _log.LogInformation($"Consensus failed timeout uncertain. start view change.");
                                if (CurrentState == BlockChainState.Almighty)
                                {
                                    _viewChangeHandler.BeginChangeView(false);
                                }
                            }
                        }
                    }
                }
                catch(Exception e)
                {
                    _log.LogError($"In Time keeper: {e}");
                }
            };
            timr.AutoReset = true;
            timr.Enabled = true;

            Task.Run(async () =>
            {
                int count = 0;

                // give other routine time to work/start/init
                await Task.Delay(30000).ConfigureAwait(false);

                while (true)
                {
                    try
                    {
                        if (_stateMachine.State == BlockChainState.Almighty 
                                || _stateMachine.State == BlockChainState.Genesis)
                        {
                            var oldList = _criticalMsgCache.Where(a => a.Value < DateTime.Now.AddSeconds(-60))
                                    .Select(b => b.Key)
                                    .ToList();

                            foreach(var hb in oldList)
                            {
                                _criticalMsgCache.TryRemove(hb, out _);
                            }
                        }

                        await HeartBeatAsync();

                        if (_stateMachine.State == BlockChainState.Almighty)
                        {
                            await CreateConsolidationBlock();
                        }

                        count++;

                        if (count > 4 * 5)     // 5 minutes
                        {
                            count = 0;
                        }                        
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning("In maintaince loop: " + ex.ToString());
                    }
                    finally
                    {
                        await Task.Delay(15000).ConfigureAwait(false);
                    }
                }
            });
        }

        internal void GotViewChangeRequest(long viewId)
        {
            if (CurrentState == BlockChainState.Almighty)
            {
                _log.LogWarning($"GotViewChangeRequest from other nodes for {viewId}");
                _viewChangeHandler.BeginChangeView(true);
            }
            else
                _log.LogWarning($"GotViewChangeRequest for CurrentState: {CurrentState}");
        }

        private void CreateStateMachine()
        {
            _stateMachine.Configure(BlockChainState.NULL)
                .Permit(BlockChainTrigger.LocalNodeStartup, BlockChainState.Initializing);

            _stateMachine.Configure(BlockChainState.Initializing)
                .OnEntry(async () =>
                {                 
                    _log.LogInformation($"Consensus Service Startup... ");

                    while(true)
                    {
                        try
                        {
                            ServiceBlock lsb = null;

                            // try get lsb from seed0
                            var seed0 = GetClientForSeeds();
                            var result = await seed0.GetLastServiceBlock();
                            if (result.ResultCode == APIResultCodes.Success)
                            {
                                lsb = result.GetBlock() as ServiceBlock;
                            }

                            if (lsb == null)
                            {
                                _board.CurrentLeader = ProtocolSettings.Default.StandbyValidators[0];          // default to seed0
                                _board.UpdatePrimary(ProtocolSettings.Default.StandbyValidators.ToList());        // default to seeds
                                _board.AllVoters = _board.PrimaryAuthorizers;                           // default to all seed nodes
                            }
                            else
                            {
                                _board.CurrentLeader = lsb.Leader;
                                _board.UpdatePrimary(lsb.Authorizers.Keys.ToList());
                                _board.AllVoters = _board.PrimaryAuthorizers;
                            }

                            break;
                        }
                        catch(Exception ex)
                        {
                            _log.LogInformation($"Consensus Service Startup Exception: {ex.Message}");
                        }
                    }

                    // swith mode
                    _stateMachine.Fire(BlockChainTrigger.DatabaseSync);
                })
                .Permit(BlockChainTrigger.DatabaseSync, BlockChainState.StaticSync);

            _stateMachine.Configure(BlockChainState.StaticSync)
                .OnEntry(() => Task.Run(async () =>
                {                    
                    while (true)
                    {
                        try
                        {
                            _log.LogInformation($"Querying Lyra Network Status... ");

                            _nodeStatus.Clear();
                            var inq = new ChatMsg("", ChatMessageType.NodeStatusInquiry);
                            inq.From = _sys.PosWallet.AccountId;
                            await Send2P2pNetworkAsync(inq);

                            await Task.Delay(5000);

                            var currentMajority = GetNetworkMajority();

                            if (currentMajority.Any())
                            {
                                var majorHeight = currentMajority.First().totalBlockCount;

                                var majority = 3;// LyraGlobal.GetMajority(_board.ActiveNodes.Count);
                                _log.LogInformation($"CheckInquiryResult: Major Height = {majorHeight} of {currentMajority.Count} majority {majority}");

                                var myStatus = await GetNodeStatusAsync();
                                
                                if (myStatus.totalBlockCount == 0 && majorHeight == 0 && currentMajority.Count >= majority)
                                {
                                    //_stateMachine.Fire(_engageTriggerStartupSync, majorHeight.Height);
                                    _stateMachine.Fire(BlockChainTrigger.ConsensusBlockChainEmpty);
                                    break;
                                }
                                else if (majorHeight >= 2 && currentMajority.Count >= majority)
                                {
                                    // verify local database
                                    while (!await SyncDatabase(false))
                                    {
                                        //fatal error. should not run program
                                        _log.LogCritical($"Unable to sync blockchain database. Will retry in 1 minute.");
                                        await Task.Delay(60000);
                                    }
                                    _stateMachine.Fire(_engageTriggerStart, majorHeight);
                                    break;
                                }
                                else
                                {
                                    continue;
                                }
                            }
                            else
                            {
                                _log.LogInformation($"Unable to get Lyra network status.");
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.LogCritical("In BlockChainState.Startup: " + ex.ToString());
                        }
                    }
                }))
                .PermitReentry(BlockChainTrigger.QueryingConsensusNode)
                .Permit(BlockChainTrigger.ConsensusBlockChainEmpty, BlockChainState.Genesis)
                .Permit(BlockChainTrigger.ConsensusNodesInitSynced, BlockChainState.Engaging);

            _stateMachine.Configure(BlockChainState.Genesis)
                .OnEntry(() => Task.Run(async () =>
                {
                    LocalDbSyncState.Remove();

                    var IsSeed0 = _sys.PosWallet.AccountId == ProtocolSettings.Default.StandbyValidators[0];
                    if (await _sys.Storage.FindLatestBlockAsync() == null && IsSeed0)
                    {
                        // check if other seeds is ready
                        do
                        {
                            _log.LogInformation("Check if other node is in genesis mode.");
                            await Task.Delay(3000);
                        } while (_board.ActiveNodes
                            .Where(a => _board.PrimaryAuthorizers.Contains(a.AccountID))
                            .Where(a => a.State == BlockChainState.Genesis)
                            .Count() < 4);
                        await GenesisAsync();
                    }
                    else
                    {
                        // wait for genesis to finished.
                        await Task.Delay(360000);
                    }
                }))
                .Permit(BlockChainTrigger.GenesisDone, BlockChainState.Almighty);

            _stateMachine.Configure(BlockChainState.Engaging)
                .OnEntryFrom(_engageTriggerStart, (blockCount) => Task.Run(async () =>
                {
                    try
                    {
                        if (blockCount > 2)
                        {
                            // sync cons and uncons
                            await EngagingSyncAsync(true);
                        }
                    }
                    catch (Exception e)
                    {
                        _log.LogError($"In Engaging: {e}");
                    }

                    _stateMachine.Fire(BlockChainTrigger.LocalNodeFullySynced);
                }))
                .Permit(BlockChainTrigger.LocalNodeFullySynced, BlockChainState.Almighty);

            _stateMachine.Configure(BlockChainState.Almighty)
                .OnEntry(() => Task.Run(async () =>
                {
                    await DeclareConsensusNodeAsync();
                    //await Task.Delay(35000);    // wait for enough heartbeat
                    //RefreshAllNodesVotes();
                }))
                .Permit(BlockChainTrigger.LocalNodeOutOfSync, BlockChainState.StaticSync)
                .Permit(BlockChainTrigger.LocalNodeMissingBlock, BlockChainState.Engaging);

            _stateMachine.OnTransitioned(t =>
            {
                _sys.UpdateConsensusState(t.Destination);
                _log.LogWarning($"OnTransitioned: {t.Source} -> {t.Destination} via {t.Trigger}({string.Join(", ", t.Parameters)})");
            });
        }

        public int GetQualifiedNodeCount(List<ActiveNode> allNodes)
        {
            var count = allNodes.Count();
            if (count > LyraGlobal.MAXIMUM_VOTER_NODES)
            {
                return LyraGlobal.MAXIMUM_VOTER_NODES;
            }
            else if (count < LyraGlobal.MINIMUM_AUTHORIZERS)
            {
                return LyraGlobal.MINIMUM_AUTHORIZERS;
            }
            else
            {
                return count;
            }
        }

        public List<string> LookforVoters()
        {
            // TODO: filter auth signatures
            var list = Board.ActiveNodes.ToList()   // make sure it not changed any more
                .Where(a => a.Votes >= LyraGlobal.MinimalAuthorizerBalance)
                .OrderByDescending(a => a.Votes)
                .ThenBy(a => a.AccountID)
                .ToList();

            var list2 = list.Take(GetQualifiedNodeCount(list))
                .Select(a => a.AccountID)
                .ToList();
            return list2;
        }

        public void UpdateVoters()
        {
            _log.LogInformation("UpdateVoters begin...");
            RefreshAllNodesVotes();
            Board.AllVoters = LookforVoters();
            _log.LogInformation("UpdateVoters ended.");
        }

        internal void ConsolidationSucceed(ConsolidationBlock cons)
        {
            _ = Task.Run(async () => {
                _log.LogInformation($"We have a new consolidation block: {cons.Hash.Shorten()}");
                var lsb = await _sys.Storage.GetLastServiceBlockAsync();

                if (CurrentState == BlockChainState.Genesis)
                {
                    var localState = LocalDbSyncState.Load();
                    localState.databaseVersion = LyraGlobal.DatabaseVersion;
                    localState.svcGenHash = lsb.Hash;
                    localState.lastVerifiedConsHeight = cons.Height;
                    LocalDbSyncState.Save(localState);

                    _stateMachine.Fire(BlockChainTrigger.GenesisDone);
                    return;
                }

                if (CurrentState == BlockChainState.Almighty)
                {
                    var localState = LocalDbSyncState.Load();
                    if(localState.lastVerifiedConsHeight == 0)
                    {
                        localState.databaseVersion = LyraGlobal.DatabaseVersion;
                        localState.svcGenHash = lsb.Hash;
                    }
                    localState.lastVerifiedConsHeight = cons.Height;
                    LocalDbSyncState.Save(localState);
                }

                var list1 = lsb.Authorizers.Keys.ToList();
                UpdateVoters();
                var list2 = Board.AllVoters;

                var firstNotSecond = list1.Except(list2).ToList();
                var secondNotFirst = list2.Except(list1).ToList();

                if (!firstNotSecond.Any() && !secondNotFirst.Any())
                {
                    _log.LogInformation($"voter list is same as previous one.");
                    return;
                }

                if (CurrentState == BlockChainState.Almighty)
                {
                    _log.LogInformation($"We have new player(s). Change view...");
                    // should change view for new member
                    _viewChangeHandler.BeginChangeView(false);
                }
                else
                {
                    _log.LogInformation($"Current state {CurrentState} not allow to change view.");
                }
            });
        }

        internal void ServiceBlockCreated(ServiceBlock sb)
        {
            Board.UpdatePrimary(sb.Authorizers.Keys.ToList());
            Board.CurrentLeader = sb.Leader;
            _viewChangeHandler.ShiftView(sb.Height + 1);
        }

        internal void LocalConsolidationFailed(string hash)
        {
            if (CurrentState == BlockChainState.Almighty)
                _stateMachine.Fire(_engageTriggerConsolidateFailed, hash);
        }

        internal void LocalServiceBlockFailed(string hash)
        {
            if (CurrentState == BlockChainState.Almighty)
                _stateMachine.Fire(BlockChainTrigger.LocalNodeMissingBlock);
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

        public virtual async Task Send2P2pNetworkAsync(SourceSignedMessage item)
        {
            item.Sign(_sys.PosWallet.PrivateKey, item.From);
            //_log.LogInformation($"Sending message type {item.MsgType} Hash {(item as BlockConsensusMessage)?.BlockHash}");
            //_localNode.Tell(item);
            await CriticalRelayAsync(item, null);
        }

        private async Task<ActiveNode> DeclareConsensusNodeAsync()
        {
            // declare to the network
            PosNode me = new PosNode(_sys.PosWallet.AccountId);
            me.NodeVersion = LyraGlobal.NODE_VERSION.ToString();
            _myIpAddress = await GetPublicIPAddress.PublicIPAddressAsync(Settings.Default.LyraNode.Lyra.NetworkId);
            me.IPAddress = $"{_myIpAddress}";

            var lastSb = await _sys.Storage.GetLastServiceBlockAsync();
            var signAgainst = lastSb?.Hash ?? ProtocolSettings.Default.StandbyValidators[0];

            me.AuthorizerSignature = Signatures.GetSignature(_sys.PosWallet.PrivateKey,
                    signAgainst, _sys.PosWallet.AccountId);

            var msg = new ChatMsg(JsonConvert.SerializeObject(me), ChatMessageType.NodeUp);
            msg.From = _sys.PosWallet.AccountId;
            await Send2P2pNetworkAsync(msg);

            // add self to active nodes list
            if(_board.NodeAddresses.ContainsKey(me.AccountID))
            {
                _board.NodeAddresses[me.AccountID] = me.IPAddress.ToString();
            }
            else
                _board.NodeAddresses.TryAdd(me.AccountID, me.IPAddress.ToString());
            await OnNodeActive(me.AccountID, me.AuthorizerSignature, _stateMachine.State);

            return _board.ActiveNodes.FirstOrDefault(a => a.AccountID == me.AccountID);
        }

        private async Task OnHeartBeatAsync(HeartBeatMessage heartBeat)
        {
            // dq any lower version
            var ver = new Version(heartBeat.NodeVersion);
            if(string.IsNullOrWhiteSpace(heartBeat.NodeVersion) || LyraGlobal.MINIMAL_COMPATIBLE_VERSION.CompareTo(ver) > 0)
            {
                //_log.LogInformation($"Node {heartBeat.From.Shorten()} ver {heartBeat.NodeVersion} is too old. Need at least {LyraGlobal.MINIMAL_COMPATIBLE_VERSION}");
                return;
            }

            await OnNodeActive(heartBeat.From, heartBeat.AuthorizerSignature, heartBeat.State, heartBeat.PublicIP);
        }

        private async Task OnNodeActive(string accountId, string authSign, BlockChainState state, string ip = null)
        {
            var lastSb = await _sys.Storage.GetLastServiceBlockAsync();
            var signAgainst = lastSb?.Hash ?? ProtocolSettings.Default.StandbyValidators[0];

            if (_board.ActiveNodes.ToArray().Any(a => a.AccountID == accountId))
            {
                var node = _board.ActiveNodes.First(a => a.AccountID == accountId);
                node.LastActive = DateTime.Now;
                node.State = state;
                node.AuthorizerSignature = authSign;
            }
            else
            {
                var node = new ActiveNode
                {
                    AccountID = accountId,
                    State = state,
                    LastActive = DateTime.Now,
                    AuthorizerSignature = authSign
                };
                _board.ActiveNodes.Add(node);
            }

            if (!string.IsNullOrWhiteSpace(ip))
            {
                System.Net.IPAddress addr;
                if (System.Net.IPAddress.TryParse(ip, out addr))
                {
                    _board.NodeAddresses.AddOrUpdate(accountId, ip, (key, oldValue) => ip);
                }
            }

            _board.ActiveNodes.RemoveAll(a => a.LastActive < DateTime.Now.AddSeconds(-60));

            if (_viewChangeHandler.IsViewChanging)
                return;

            /// check if leader is online. otherwise call view-change.
            /// the uniq tick: seed0's heartbeat.
            /// if seed0 not active, then seed2,seed3, etc.
            /// if all seeds are offline, what the...
            string tickSeedId = null;
            for(int i = 0; i < ProtocolSettings.Default.StandbyValidators.Length; i++)
            {
                if (Board.ActiveNodes.Any(a => a.AccountID == ProtocolSettings.Default.StandbyValidators[i]))
                {
                    tickSeedId = ProtocolSettings.Default.StandbyValidators[i];
                    break;
                }
            }

            if(tickSeedId != null)
            {
                if (CurrentState == BlockChainState.Almighty && accountId == tickSeedId)
                {
                    if (_viewChangeHandler.TimeStarted == DateTime.MinValue && !Board.ActiveNodes.Any(a => a.AccountID == lastSb.Leader))
                    {
                        // leader is offline. we need chose one new
                        UpdateVoters();

                        _log.LogInformation($"We have no leader online. Change view...");
                        // should change view for new member
                        _viewChangeHandler.BeginChangeView(false);
                    }
                }
            }
        }

        private async Task HeartBeatAsync()
        {
            // this keep the node up pace
            //var lsb = await _sys.Storage.GetLastServiceBlockAsync();
            //if(lsb == null)
            //{
            //    _board.CurrentLeader = ProtocolSettings.Default.StandbyValidators[0];
            //    _board.LeaderCandidate = ProtocolSettings.Default.StandbyValidators[0];
            //    _board.UpdatePrimary(ProtocolSettings.Default.StandbyValidators.ToList());                
            //}
            //else
            //{
            //    _board.CurrentLeader = lsb.Leader;
            //    _board.UpdatePrimary(lsb.Authorizers.Keys.ToList());
            //}

            var me = _board.ActiveNodes.FirstOrDefault(a => a.AccountID == _sys.PosWallet.AccountId);
            if (me == null)
                me = await DeclareConsensusNodeAsync();

            me = _board.ActiveNodes.FirstOrDefault(a => a.AccountID == _sys.PosWallet.AccountId);
            if (me == null)
            {
                _log.LogError("No me in billboard!!!");
            }
            else
            {
                me.State = _stateMachine.State;
                me.LastActive = DateTime.Now;
            }

            if (_board.ActiveNodes.ToArray().Any(a => a.AccountID == _sys.PosWallet.AccountId))
                _board.ActiveNodes.First(a => a.AccountID == _sys.PosWallet.AccountId).LastActive = DateTime.Now;

            // declare to the network
            var lastSb = await _sys.Storage.GetLastServiceBlockAsync();
            var signAgainst = lastSb?.Hash ?? ProtocolSettings.Default.StandbyValidators[0];
            var msg = new HeartBeatMessage
            {
                From = _sys.PosWallet.AccountId,
                NodeVersion = LyraGlobal.NODE_VERSION.ToString(),
                Text = "I'm live",
                State = _stateMachine.State,
                PublicIP = _myIpAddress?.ToString() ?? "",
                AuthorizerSignature = Signatures.GetSignature(_sys.PosWallet.PrivateKey, signAgainst, _sys.PosWallet.AccountId)
            };

            await Send2P2pNetworkAsync(msg);
        }

        private async Task OnNodeUpAsync(ChatMsg chat)
        {
            // must throttle on this msg

            _log.LogInformation($"OnNodeUpAsync: Node is up: {chat.From.Shorten()}");

            try
            {
                if (_board == null)
                    return;

                var node = chat.Text.UnJson<PosNode>();

                // dq any lower version
                if (string.IsNullOrWhiteSpace(node.NodeVersion))
                    return;

                var ver = new Version(node.NodeVersion);
                if (LyraGlobal.MINIMAL_COMPATIBLE_VERSION.CompareTo(ver) > 0)
                {
                    //_log.LogInformation($"Node {chat.From.Shorten()} ver {node.NodeVersion} is too old. Need at least {LyraGlobal.MINIMAL_COMPATIBLE_VERSION}");
                    return;
                }

                // verify signature
                if (string.IsNullOrWhiteSpace(node.IPAddress))
                    return;

                await OnNodeActive(node.AccountID, node.AuthorizerSignature, BlockChainState.StaticSync);

                // add network/ip verifycation here
                // if(verifyIP)
                // {}

                if (_board.NodeAddresses.ContainsKey(node.AccountID))
                    _board.NodeAddresses[node.AccountID] = node.IPAddress;
                else
                    _board.NodeAddresses.TryAdd(node.AccountID, node.IPAddress);
            }
            catch (Exception ex)
            {
                _log.LogWarning($"OnNodeUpAsync: {ex.ToString()}");
            }
        }

        public bool FindActiveBlock(string accountId, long index)
        {
            return false;
            //return _activeConsensus.Values.Where(s => s.State != null)
            //    .Select(t => t.State.InputMsg.Block as TransactionBlock)
            //    .Where(x => x != null)
            //    .Any(a => a.AccountID == accountId && a.Index == index && a is SendTransferBlock);
        }

        private async Task CreateConsolidationBlock()
        {
            if (_stateMachine.State != BlockChainState.Almighty)
                return;

            if (_activeConsensus.Values.Count > 0 && _activeConsensus.Values.Any(a => a.State?.InputMsg.Block is ConsolidationBlock))
                return;

            var lastCons = await _sys.Storage.GetLastConsolidationBlockAsync();
            if (lastCons == null)
                return;         // wait for genesis

            try
            {                
                if (IsThisNodeLeader && _stateMachine.State == BlockChainState.Almighty)
                {
                    //// test code
                    //var livingPosNodeIds = _board.AllNodes.Keys.ToArray();
                    //_lastVotes = _sys.Storage.FindVotes(livingPosNodeIds);
                    //// end test code
                    bool allNodeSyncd = false;
                    try
                    {
                        allNodeSyncd = true;// await CheckPrimaryNodesStatus();
                    }
                    catch(Exception ex)
                    {
                        _log.LogWarning("Exception in CheckPrimaryNodesStatus: " + ex.ToString());
                    }
                    if(allNodeSyncd)
                    {
                        // consolidate time from lastcons to now - 10s
                        var timeStamp = DateTime.UtcNow.AddSeconds(-10);
                        var unConsList = await _sys.Storage.GetBlockHashesByTimeRange(lastCons.TimeStamp, timeStamp);

                        // if 1 it must be previous consolidation block.
                        if (unConsList.Count() >= 10 || (unConsList.Count() > 1 && DateTime.UtcNow - lastCons.TimeStamp > TimeSpan.FromMinutes(10)))
                        {
                            try
                            {
                                await CreateConsolidateBlockAsync(lastCons, timeStamp, unConsList);
                            }
                            catch (Exception ex)
                            {
                                _log.LogError($"In creating consolidation block: {ex.Message}");
                            }
                        }
                    }
                    else
                    {
                        // so there is inconsistant between seed0 and other nodes.

                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogError("Error In GenerateConsolidateBlock: " + ex.Message);
            }
            finally
            {
                
            }
        }

        //private async Task<bool> CheckPrimaryNodesStatus()
        //{
        //    if (Board.AllNodes.Count < 4)
        //        return false;

        //    var bag = new ConcurrentDictionary<string, GetSyncStateAPIResult>();
        //    var tasks = Board.AllNodes
        //        .Where(a => Board.PrimaryAuthorizers.Contains(a.AccountID))  // exclude self
        //        .Select(b => b)
        //        .Select(async node =>
        //        {
        //            var lcx = LyraRestClient.Create(Neo.Settings.Default.LyraNode.Lyra.NetworkId, Environment.OSVersion.ToString(), "Seed0", "1.0", $"http://{node.IPAddress}:{Neo.Settings.Default.P2P.WebAPI}/api/Node/");
        //            try
        //            {
        //                var syncState = await lcx.GetSyncState();
        //                bag.TryAdd(node.AccountID, syncState);
        //            }
        //            catch (Exception ex)
        //            {
        //                bag.TryAdd(node.AccountID, null);
        //            }
        //        });
        //    await Task.WhenAll(tasks);
        //    var mySyncState = bag[_sys.PosWallet.AccountId];
        //    var q = bag.Where(a => a.Key != _sys.PosWallet.AccountId)
        //        .Select(a => a.Value)
        //        .GroupBy(x => x.Status.lastUnSolidationHash)
        //        .Select(g => new { Hash = g.Key, Count = g.Count() })
        //        .OrderByDescending(x => x.Count)
        //        .First();

        //    if(mySyncState.Status.lastUnSolidationHash == q.Hash && q.Count > (int)Math.Ceiling((double)(Board.PrimaryAuthorizers.Length) * 3 / 2))
        //    {
        //        return true;
        //    }
        //    else
        //    {
        //        return false;
        //    }
        //}

        private async Task CreateConsolidateBlockAsync(ConsolidationBlock lastCons, DateTime timeStamp, IEnumerable<string> collection)
        {
            _log.LogInformation($"Creating ConsolidationBlock... ");

            var consBlock = new ConsolidationBlock
            {
                createdBy = _sys.PosWallet.AccountId,
                blockHashes = collection.ToList(),
                totalBlockCount = lastCons.totalBlockCount + collection.Count()
            };
            consBlock.TimeStamp = timeStamp;

            var mt = new MerkleTree();
            decimal feeAggregated = 0;
            foreach (var hash in consBlock.blockHashes)
            {
                mt.AppendLeaf(MerkleHash.Create(hash));

                // aggregate fees
                var transBlock = (await _sys.Storage.FindBlockByHashAsync(hash)) as TransactionBlock;
                if (transBlock != null)
                {
                    feeAggregated += transBlock.Fee;
                }
            }

            consBlock.totalFees = feeAggregated.ToBalanceLong();
            consBlock.MerkelTreeHash = mt.BuildTree().ToString();
            consBlock.ServiceHash = (await _sys.Storage.GetLastServiceBlockAsync()).Hash;

            consBlock.InitializeBlock(lastCons, _sys.PosWallet.PrivateKey,
                _sys.PosWallet.AccountId);

            AuthorizingMsg msg = new AuthorizingMsg
            {
                IsServiceBlock = false,
                From = _sys.PosWallet.AccountId,
                BlockHash = consBlock.Hash,
                Block = consBlock,
                MsgType = ChatMessageType.AuthorizerPrePrepare
            };

            var state = new AuthState(true);
            state.SetView(Board.PrimaryAuthorizers);
            state.InputMsg = msg;

            //_ = Task.Run(async () =>
            //{
            //    _log.LogInformation($"Waiting for ConsolidateBlock authorizing...");

            //    await state.Done.AsTask();
            //    state.Done.Close();
            //    state.Done = null;

            //    if (state.CommitConsensus == ConsensusResult.Yea)
            //    {
            //        _log.LogInformation($"ConsolidateBlock is OK. update vote stats.");

            //        RefreshAllNodesVotes();
            //    }
            //    else
            //    {
            //        _log.LogInformation($"ConsolidateBlock is Failed. vote stats not updated.");
            //    }
            //});

            await SubmitToConsensusAsync(state);

            _log.LogInformation($"ConsolidationBlock was submited. ");
        }

        public static Props Props(DagSystem sys, IActorRef localNode, IActorRef blockchain)
        {
            return Akka.Actor.Props.Create(() => new ConsensusService(sys, localNode, blockchain)).WithMailbox("consensus-service-mailbox");
        }

        private async Task SubmitToConsensusAsync(AuthState state)
        {
            if (state.InputMsg?.Block?.BlockType == BlockTypes.SendTransfer)
            {
                var tx = state.InputMsg.Block as TransactionBlock;
                var allSend = _activeConsensus.Values.Where(a => a.State?.InputMsg?.Block?.BlockType == BlockTypes.SendTransfer)
                    .Select(x => x.State.InputMsg.Block as TransactionBlock);

                var sameHeight = allSend.Any(y => y.AccountID == tx.AccountID && y.Height == tx.Height);
                var sameHash = _activeConsensus.Values.Any(a => a.State?.InputMsg.Hash == tx.Hash);
                if (sameHeight || sameHash)
                {
                    _log.LogCritical($"double spend detected: {tx.AccountID} Height: {tx.Height} Hash: {tx.Hash}");
                    return;
                }
            }

            var worker = await GetWorkerAsync(state.InputMsg.Block.Hash);
            if(worker != null)
            {
                await worker.ProcessState(state);
            }
            await Send2P2pNetworkAsync(state.InputMsg);
        }

        private async Task<ConsensusWorker> GetWorkerAsync(string hash, bool checkState = false)
        {
            // if a block is in database
            var aBlock = await _sys.Storage.FindBlockByHashAsync(hash);
            if (aBlock != null)
            {
                //_log.LogWarning($"GetWorker: already in database! hash: {hash.Shorten()}");
                return null;
            }

            if(_activeConsensus.ContainsKey(hash))
                return _activeConsensus[hash];
            else
            {
                ConsensusWorker worker;
                if (checkState && _stateMachine.State == BlockChainState.Almighty)
                    worker = new ConsensusWorker(this, hash);
                else if (checkState && _stateMachine.State == BlockChainState.Engaging)
                    worker = new ConsensusEngagingWorker(this, hash);
                else
                    worker = new ConsensusWorker(this, hash);

                worker.OnConsensusSuccess += Worker_OnConsensusSuccess;

                if (_activeConsensus.TryAdd(hash, worker))
                    return worker;
                else
                    return _activeConsensus[hash];
            }
        }

        private void Worker_OnConsensusSuccess(ConsensusHandlerBase handler, Block block)
        {
            _successBlockCount++;
            _activeConsensus.TryRemove(block.Hash, out _);
            _log.LogInformation($"Finished consensus: {_successBlockCount} Active Consensus: {_activeConsensus.Count}");
        }

        private async Task<bool> CriticalRelayAsync<T>(T message, Func<T, Task> localAction)
            where T : SourceSignedMessage, new()
        {
            //_log.LogInformation($"OnRelay: {message.MsgType} From: {message.From.Shorten()} Hash: {(message as BlockConsensusMessage)?.BlockHash} My state: {CurrentState}");

            // seed node relay heartbeat, only once
            // this keep the whole network one consist view of active nodes.
            // this is important to make election.
            if (_criticalMsgCache.TryAdd(message.Signature, DateTime.Now))
            {
                // try ever node forward.
                // monitor network traffic closely.

                _localNode.Tell(message);     // no sign again!!!

                if(localAction != null)
                    await localAction(message);
                return true;
            }
            else
                return false;
        }

        async Task OnNextConsensusMessageAsync(SourceSignedMessage item)
        {
            //_log.LogInformation($"OnMessage: {item.MsgType} From: {item.From.Shorten()} Hash: {(item as BlockConsensusMessage)?.BlockHash} My state: {CurrentState}");
            if (item is ChatMsg chatMsg)
            {
                await OnRecvChatMsg(chatMsg);
                return;
            }

            if (item is ViewChangeMessage vcm)
            {
                // need to listen to any view change event.
                if(/*_viewChangeHandler.IsViewChanging && */CurrentState == BlockChainState.Almighty && Board.ActiveNodes.Any(a => a.AccountID == vcm.From))
                {
                    await _viewChangeHandler.ProcessMessage(vcm);
                }
                return;
            }
            else if (_stateMachine.State == BlockChainState.Genesis ||
                _stateMachine.State == BlockChainState.Engaging ||
                _stateMachine.State == BlockChainState.Almighty)
            {
                if (item is BlockConsensusMessage cm)
                {
                    var worker = await GetWorkerAsync(cm.BlockHash, true);
                    if (worker != null)
                    {
                       await worker.ProcessMessage(cm);           
                    }                        
                    return;
                }
            }
        }

        private async Task OnRecvChatMsg(ChatMsg chat)
        {
            switch(chat.MsgType)
            {
                case ChatMessageType.HeartBeat:
                    await OnHeartBeatAsync(chat as HeartBeatMessage);
                    break;
                case ChatMessageType.NodeUp:
                    await Task.Run(async () => { await OnNodeUpAsync(chat); });                    
                    break;
                case ChatMessageType.NodeStatusInquiry:
                    var status = await GetNodeStatusAsync();
                    var resp = new ChatMsg(JsonConvert.SerializeObject(status), ChatMessageType.NodeStatusReply);
                    resp.From = _sys.PosWallet.AccountId;
                    await Send2P2pNetworkAsync(resp);
                    break;
                case ChatMessageType.NodeStatusReply:
                    var statusReply = JsonConvert.DeserializeObject<NodeStatus>(chat.Text);
                    if (statusReply != null)
                    {
                        if (Board.PrimaryAuthorizers == null || !Board.PrimaryAuthorizers.Contains(statusReply.accountId))
                            return;

                        if (Board.ActiveNodes.Any(a => a.AccountID == statusReply.accountId) && !_nodeStatus.Any(a => a.accountId == statusReply.accountId))
                            _nodeStatus.Add(statusReply);
                    }
                    break;
            }
        }

        private readonly Mutex _locker = new Mutex(false);
        public void RefreshAllNodesVotes()
        {
            try
            {
                _locker.WaitOne();
                // remove stalled nodes
                _board.ActiveNodes.RemoveAll(a => a.LastActive < DateTime.Now.AddSeconds(-40)); // 2 heartbeat + 10 s

                var livingPosNodeIds = _board.ActiveNodes.Select(a => a.AccountID).ToList();
                _lastVotes = _sys.Storage.FindVotes(livingPosNodeIds, DateTime.UtcNow);

                foreach (var node in _board.ActiveNodes.ToArray())
                {
                    var vote = _lastVotes.FirstOrDefault(a => a.AccountId == node.AccountID);
                    if (vote == null)
                    {
                        _log.LogInformation($"No (zero) vote found for {node.AccountID}");
                        node.Votes = 0;
                    }
                    else
                        node.Votes = vote.Amount;
                }
            }
            catch (Exception ex)
            {
                _log.LogError($"In RefreshAllNodesVotes: {ex}");
            }
            finally
            {
                _locker.ReleaseMutex();
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
