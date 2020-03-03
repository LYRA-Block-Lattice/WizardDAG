﻿using Akka.Actor;
using Lyra.Core.API;
using Lyra.Core.Blocks;
using Lyra.Core.Cryptography;
using Lyra.Core.Decentralize;
using Lyra.Core.Utils;
using Lyra.Shared;
using Microsoft.Extensions.Logging;
using Neo;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Lyra.Core.Decentralize
{
    public class ConsensusWorker
    {
        ConsensusService _context;
        ILogger _log;
        private AuthorizersFactory _authorizers;

        ConcurrentQueue<SourceSignedMessage> _outOfOrderedMessages;

        AuthState _state;

        public AuthState State { get => _state; set => _state = value; }

        public ConsensusWorker(ConsensusService context)
        {
            _context = context;
            _log = new SimpleLogger("ConsensusWorker").Logger;
            _authorizers = new AuthorizersFactory();
            _outOfOrderedMessages = new ConcurrentQueue<SourceSignedMessage>();
        }

        public void Create(AuthState state, WaitHandle waitHandle = null)
        {
            _state = state;
            //_log.LogInformation($"Receive AuthState: {_state.InputMsg.Block.UIndex}/{_state.InputMsg.Block.Index}/{_state.InputMsg.Block.Hash}");

            _ = Task.Run(async () =>
            {
                if (waitHandle != null)
                {
                    _log.LogWarning($"Consensus Create: Wait for previous block to get its consensus result...");
                    await waitHandle.AsTask();
                }

                _context.Send2P2pNetwork(_state.InputMsg);

                state.T1 = DateTime.Now;

                await AuthorizeAsync(_state.InputMsg);

                state.T2 = DateTime.Now;
            });
        }

        private AuthState CreateAuthringState(AuthorizingMsg item)
        {
            _log.LogInformation($"Consensus: CreateAuthringState Called: BlockUIndex: {item.Block.UIndex}");

            var ukey = item.Block.Hash;
            //if (_activeConsensus.ContainsKey(ukey))
            //{
            //    return _activeConsensus[ukey];
            //}

            var state = new AuthState
            {
                HashOfFirstBlock = ukey,
                InputMsg = item,
            };

            //// add possible out of ordered messages belong to the block
            //if (_outOfOrderedMessages.ContainsKey(item.Block.Hash))
            //{
            //    var msgs = _outOfOrderedMessages[item.Block.Hash];
            //    _outOfOrderedMessages.Remove(item.Block.Hash);

            //    foreach (var msg in msgs)
            //    {
            //        switch (msg)
            //        {
            //            case AuthorizedMsg authorized:
            //                state.AddAuthResult(authorized);
            //                break;
            //            case AuthorizerCommitMsg committed:
            //                state.AddCommitedResult(committed);
            //                break;
            //        }
            //    }
            //}

            // check if block existing
            //if (null != BlockChain.Singleton.FindBlockByHash(item.Block.Hash))
            //{
            //    _log.LogInformation("CreateAuthringState: Block is already in database.");
            //    return null;
            //}

            // check if block was replaced by nulltrans
            //if (null != BlockChain.Singleton.FindNullTransBlockByHash(item.Block.Hash))
            //{
            //    _log.LogInformation("CreateAuthringState: Block is already consolidated by nulltrans.");
            //    return null;
            //}

            //_activeConsensus.Add(ukey, state);
            return state;
        }

        private async Task<AuthorizedMsg> LocalAuthorizingAsync(AuthorizingMsg item)
        {
            //_log.LogInformation($"LocalAuthorizingAsync: {item.Block.BlockType} {item.Block.UIndex}/{item.Block.Index}/{item.Block.Hash}");
            if (!ConsensusService.Board.CanDoConsensus || !ConsensusService.AuthorizerShapshot.Contains(NodeService.Instance.PosWallet.AccountId))
            {
                var result0 = new AuthorizedMsg
                {
                    From = NodeService.Instance.PosWallet.AccountId,
                    MsgType = ChatMessageType.AuthorizerPrepare,
                    BlockHash = item.Block.Hash,
                    Result = APIResultCodes.PBFTNetworkNotReadyForConsensus
                };
                return result0;
            }

            var stopwatch = Stopwatch.StartNew();
            var authorizer = _authorizers.Create(item.Block.BlockType);

            AuthorizedMsg result;
            try
            {
                item.Block.UIndex = _context.GenSeed();
                var localAuthResult = await authorizer.AuthorizeAsync(item.Block);
                result = new AuthorizedMsg
                {
                    From = NodeService.Instance.PosWallet.AccountId,
                    MsgType = ChatMessageType.AuthorizerPrepare,
                    BlockHash = item.Block.Hash,
                    Result = localAuthResult.Item1,
                    AuthSign = localAuthResult.Item2
                };

                result.BlockUIndex = item.Block.UIndex;
                _log.LogInformation($"Give UIndex {result.BlockUIndex} to block {item.Block.Hash.Shorten()} of Type {item.Block.BlockType}");
            }
            catch (Exception e)
            {
                _log.LogWarning($"Consensus: LocalAuthorizingAsync Exception: {e.Message} BlockUIndex: {item.Block.UIndex}");

                result = new AuthorizedMsg
                {
                    From = NodeService.Instance.PosWallet.AccountId,
                    MsgType = ChatMessageType.AuthorizerPrepare,
                    BlockHash = item.Block.Hash,
                    Result = APIResultCodes.UnknownError,
                    AuthSign = null
                };
            }

            stopwatch.Stop();
            if (result.Result == APIResultCodes.Success)
                _log.LogInformation($"LocalAuthorizingAsync {item.Block.BlockType} takes {stopwatch.ElapsedMilliseconds} ms with {result.Result}");
            else
            {
                if (result.Result == APIResultCodes.CouldNotFindLatestBlock)
                {
                    _log.LogInformation($"CouldNotFindLatestBlock!! state: {_state.InputMsg.Block.UIndex}/{_state.InputMsg.Block.Index}/{_state.InputMsg.Block.Hash} Previous Block Hash: {_state.InputMsg.Block.PreviousHash}");
                }
                _log.LogError($"LocalAuthorizingAsync takes {stopwatch.ElapsedMilliseconds} ms with {result.Result}");
                _log.LogInformation($"LocalAuthorizingAsync state: {_state.InputMsg.Block.UIndex}/{_state.InputMsg.Block.Index}/{_state.InputMsg.Block.Hash}");
            }

            return result;
        }

        public async Task OnPrePrepareAsync(AuthorizingMsg msg, WaitHandle waitHandle = null)
        {
            _log.LogInformation($"Receive AuthorizingMsg: {msg.Block.UIndex}/{msg.Block.Index}/{msg.Block.Hash}");
            _context.OnNodeActive(NodeService.Instance.PosWallet.AccountId);     // update billboard

            if (msg.Version != LyraGlobal.ProtocolVersion)
            {
                return;
            }

            if (_state != null)
            {
                _log.LogError("State exists.");
                return;
            }

            // first try auth locally
            //if(_state == null)
            _state = CreateAuthringState(msg);

            SourceSignedMessage queuedMsg;
            while (_outOfOrderedMessages.TryDequeue(out queuedMsg))
            {
                switch (queuedMsg)
                {
                    case AuthorizedMsg msg1:
                        await OnPrepareAsync(msg1);
                        break;
                    case AuthorizerCommitMsg msg2:
                        await OnCommitAsync(msg2);
                        break;
                }
            }

            //_context.Send2P2pNetwork(msg);
            _ = Task.Run(async () =>
              {
                  if (waitHandle != null)
                      await waitHandle.AsTask();

                  await AuthorizeAsync(msg);
              });
        }

        private async Task AuthorizeAsync(AuthorizingMsg msg)
        {
            var localAuthResult = await LocalAuthorizingAsync(msg);

            _state.AddAuthResult(localAuthResult);
            _context.Send2P2pNetwork(localAuthResult);
            await CheckAuthorizedAllOkAsync(_state);
        }

        public async Task OnPrepareAsync(AuthorizedMsg item)
        {
            if (_state == null)
            {
                _outOfOrderedMessages.Enqueue(item);
                //_log.LogWarning($"OnPrepareAsync: _state null for {item.BlockUIndex}/{item.BlockHash.Shorten()}");
                return;
            }

            //_log.LogInformation($"OnPrepareAsync: {_state.InputMsg.Block.UIndex}/{_state.InputMsg.Block.Index}/{_state.InputMsg.Block.Hash}");

            if (_state.T3 == default)
                _state.T3 = DateTime.Now;

            //if (_activeConsensus.ContainsKey(item.BlockHash))
            //{
            //    var state = _activeConsensus[item.BlockHash];
            if (_state.AddAuthResult(item))
                await CheckAuthorizedAllOkAsync(_state);
            //}
            //else
            //{
            //    // maybe outof ordered message
            //    if (_cleanedConsensus.ContainsKey(item.BlockHash))
            //    {
            //        return;
            //    }

            //    List<SourceSignedMessage> msgs;
            //    if (_outOfOrderedMessages.ContainsKey(item.BlockHash))
            //        msgs = _outOfOrderedMessages[item.BlockHash];
            //    else
            //    {
            //        msgs = new List<SourceSignedMessage>();
            //        msgs.Add(item);
            //    }

            //    msgs.Add(item);
            //}
        }

        private async Task CheckAuthorizedAllOkAsync(AuthState state)
        {
            // check state
            // debug: show all states
            if (state.OutputMsgs.Count < AuthState.WinNumber)
            {
                return;
            }
            await state.Semaphore.WaitAsync();
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine();
                var acctId = state.InputMsg.Block is TransactionBlock ? (state.InputMsg.Block as TransactionBlock).AccountID.Shorten() : "";
                sb.AppendLine($"* Transaction From Node {acctId} Type: {state.InputMsg.Block.BlockType} Index: {state.InputMsg.Block.Index} Hash: {state.InputMsg.Block.Hash.Shorten()}");
                foreach (var msg in state.OutputMsgs.ToList())
                {
                    var seed0 = msg.From == ProtocolSettings.Default.StandbyValidators[0] ? "[seed0]" : "";
                    string me = "";
                    if (msg.From == NodeService.Instance.PosWallet.AccountId)
                        me = "[me]";
                    var voice = msg.IsSuccess ? "Yay" : "Nay";
                    var canAuth = ConsensusService.AuthorizerShapshot.Contains(msg.From);
                    sb.AppendLine($"{voice} {msg.Result} By: {msg.From.Shorten()} CanAuth: {canAuth} {seed0}{me}");
                }
                _log.LogInformation(sb.ToString());

                _log.LogInformation($"state.Consensus is {state.PrepareConsensus}");
                if (ConsensusResult.Uncertain != state.PrepareConsensus)
                {

                    _log.LogInformation($"got Semaphore. is it saving? {state.Saving}");

                    if (state.Saving)
                        return;

                    state.Saving = true;

                    state.T4 = DateTime.Now;

                    _log.LogInformation($"Saving {state.PrepareConsensus}: {_state.InputMsg.Block.UIndex}/{_state.InputMsg.Block.Index}/{_state.InputMsg.Block.Hash}");

                    var ts = DateTime.Now - state.Created;
                    if (_context.Stats.Count > 10000)
                        _context.Stats.RemoveRange(0, 2000);

                    _context.Stats.Add(new TransStats { ms = (long)ts.TotalMilliseconds, trans = state.InputMsg.Block.BlockType });

                    var block = state.InputMsg.Block;
                    if (state.PrepareConsensus == ConsensusResult.Yay)
                    {
                        // do commit
                        block.Authorizations = state.OutputMsgs.Select(a => a.AuthSign).ToList();

                        if (block.BlockType != BlockTypes.Consolidation && block.BlockType != BlockTypes.NullTransaction && block.BlockType != BlockTypes.Service)
                        {
                            // pickup UIndex
                            try
                            {
                                block.UIndex = state.ConsensusUIndex;
                            }
                            catch (Exception ex)
                            {
                                _log.LogError("Can't get UIndex. System fail: " + ex.Message);
                                return;
                            }

                            //if (!IsThisNodeSeed0 && block.UIndex != USeed - 1)
                            //{
                            //    // local node out of sync
                            //    Mode = ConsensusWorkingMode.OutofSyncWaiting;
                            //    LyraSystem.Singleton.TheBlockchain.Tell(new BlockChain.NeedSync { ToUIndex = block.UIndex });
                            //}
                        }

                        block.UHash = SignableObject.CalculateHash($"{block.UIndex}|{block.Index}|{block.Hash}");

                        var msg = new AuthorizerCommitMsg
                        {
                            From = NodeService.Instance.PosWallet.AccountId,
                            MsgType = ChatMessageType.AuthorizerCommit,
                            BlockHash = state.InputMsg.Block.Hash,
                            BlockUIndex = block.UIndex,
                            BlockUHash = block.UHash,
                            Consensus = state.PrepareConsensus,
                            Commited = true
                        };

                        _context.Send2P2pNetwork(msg);
                        state.AddCommitedResult(msg);
                    }
                    else
                    {
                        // nay
                        //var nb = new NullTransactionBlock
                        //{
                        //    UIndex = state.ConsensusUIndex,
                        //    FailedBlockHash = block.Hash,
                        //    ServiceHash = block.ServiceHash,
                        //    AccountID = block.AccountID
                        //};
                        //nb.InitializeBlock(null, NodeService.Instance.PosWallet.PrivateKey,
                        //    NodeService.Instance.PosWallet.AccountId);
                        //nb.UHash = SignableObject.CalculateHash($"{nb.UIndex}|{nb.Index}|{nb.Hash}");

                        //if (await BlockChain.Singleton.AddBlockAsync(block))
                        //    _log.LogInformation($"NullTrans saved: {nb.UIndex}");
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogError($"CheckAuthorizedAllOkAsync: {ex.ToString()}");
            }
            finally
            {
                state.Semaphore.Release();
            }
        }

        private async Task CheckCommitedOKAsync()
        {
            var block = _state.InputMsg.Block;
            if (_state.CommitConsensus == ConsensusResult.Yay)
            {
                if (!await BlockChain.Singleton.AddBlockAsync(block))
                    _log.LogWarning($"Block Save Failed UIndex: {block.UIndex}");
                else
                    _log.LogInformation($"Block saved: {block.UIndex}/{block.Index}/{block.Hash}");

                _state.Done?.Set();
                _context.FinishBlock(_state.HashOfFirstBlock);

                if(block is ConsolidationBlock)
                {
                    // get my authorize result
                    var myResult = _state.OutputMsgs.FirstOrDefault(a => a.From == NodeService.Instance.PosWallet.AccountId);
                    if (myResult != null && myResult.Result == APIResultCodes.Success)
                        return;

                    // crap! this node is out of sync.
                    LyraSystem.Singleton.Consensus.Tell(new ConsensusService.ConsolidateFailed { consolidationBlockHash = block.Hash });
                }
            }
        }

        public async Task OnCommitAsync(AuthorizerCommitMsg item)
        {
            if (_state == null)
            {
                _outOfOrderedMessages.Enqueue(item);
                //_log.LogWarning($"OnCommit: _state null for {item.BlockHash.Shorten()}");
                return;
            }

            if (_state.T5 == default)
                _state.T5 = DateTime.Now;

            _log.LogInformation($"OnCommit: {_state.InputMsg.Block.UIndex}/{_state.InputMsg.Block.Index}/{_state.InputMsg.Block.Hash}");

            //if (_activeConsensus.ContainsKey(item.BlockHash))
            //{
            //    var state = _activeConsensus[item.BlockHash];
            if(_state.AddCommitedResult(item))
                await CheckCommitedOKAsync();

            _context.OnNodeActive(item.From);        // track latest activities via billboard
            //}
            //else
            //{
            //    // maybe outof ordered message
            //    if (_cleanedConsensus.ContainsKey(item.BlockHash))
            //    {
            //        return;
            //    }

            //    List<SourceSignedMessage> msgs;
            //    if (_outOfOrderedMessages.ContainsKey(item.BlockHash))
            //        msgs = _outOfOrderedMessages[item.BlockHash];
            //    else
            //    {
            //        msgs = new List<SourceSignedMessage>();
            //        msgs.Add(item);
            //    }

            //    msgs.Add(item);
            //}
        }
    }
}
