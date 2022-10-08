﻿using Lyra.Core.API;
using Lyra.Core.Blocks;
using Lyra.Core.Decentralize;
using Lyra.Data.API;
using Lyra.Data.Blocks;
using Microsoft.Extensions.Logging;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Serializers;
using Neo;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WorkflowCore.Interface;
using WorkflowCore.Models;
using WorkflowCore.Services;

namespace Lyra.Core.WorkFlow
{
    public class Repeator : StepBodyAsync
    {
        public TransactionBlock block { get; set; }
        public int count { get; set; }

        private ILogger _logger;

        public Repeator(ILogger<Repeator> logger)
        {
            _logger = logger;
        }

        public override async Task<ExecutionResult> RunAsync(IStepExecutionContext context)
        {
            var ctx = context.Workflow.Data as LyraContext;

            _logger.LogInformation($"In Repeator, State: {ctx.State } Key: {ctx.SendHash}");
            try
            {
                var SubWorkflow = BrokerFactory.DynWorkFlows[ctx.SvcRequest];

                SendTransferBlock? sendBlock = null;

                sendBlock = await DagSystem.Singleton.Storage.FindBlockByHashAsync(ctx.SendHash)
                    as SendTransferBlock;

                if (sendBlock == null)
                {
                    _logger.LogCritical($"Fatal: Workflow can't find the key send block: {ctx.SendHash}");
                    block = null;
                    ctx.State = WFState.Error;
                }
                else
                {
                    block =
                        await BrokerOperations.ReceiveViaCallback[SubWorkflow.GetDescription().RecvVia](DagSystem.Singleton, sendBlock)
                            ??
                        await SubWorkflow.MainProcAsync(DagSystem.Singleton, sendBlock, ctx);

                    _logger.LogInformation($"Key is ({DateTime.Now:mm:ss.ff}): {ctx.SendHash}, {ctx.Count}/, BrokerOpsAsync called and generated {block}");

                    if (block != null)
                    {
                        count++;
                        if (!block.ContainsTag(Block.MANAGEDTAG))
                            throw new Exception("Missing MANAGEDTAG");

                        if (!Enum.TryParse(block.Tags[Block.MANAGEDTAG], out WFState mgdstate))
                            throw new Exception("Invalid MANAGEDTAG");

                        ctx.State = mgdstate;
                    }
                    else
                        ctx.State = WFState.Finished;
                }
            }
            catch (Exception ex)
            {
                _logger.LogCritical($"Fatal: Workflow can't generate block: {ex}");
                block = null;
                ctx.State = WFState.Error;
            }

            return ExecutionResult.Next();
        }
    }

    public enum WFState { Init, Running, Finished, ConsensusTimeout, Error, Exited };

    public class ContextSerializer : SerializerBase<LyraContext>
    {
        public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, LyraContext value)
        {
            base.Serialize(context, args, value);
        }

        public override LyraContext Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
        {
            return base.Deserialize(context, args);
        }
    }

    [BsonIgnoreExtraElements]
    public class LyraContext
    {
        public string OwnerAccountId { get; set; }
        public string SvcRequest { get; set; }
        public string SendHash { get; set; }

        public WFState State { get; set; }

        public BlockTypes LastBlockType { get; set; }
        public string LastBlockJson { get; set; }
        public ConsensusResult? LastResult { get; set; }
        public long TimeTicks { get; set; }

        public int Count { get; set; }
        public int ViewChangeReqCount { get; set; }

        public TransactionBlock GetLastBlock()
        {
            if (LastBlockType == BlockTypes.Null)
                return null;

            var br = new BlockAPIResult
            {
                ResultBlockType = LastBlockType,
                BlockData = LastBlockJson,
            };
            return br.GetBlock() as TransactionBlock;
        }
        //public void SetLastBlock(TransactionBlock block)
        //{
        //    if (block == null)
        //    {
        //        LastBlockType = BlockTypes.Null;
        //    }
        //    else
        //    {
        //        LastBlockType = block.GetBlockType();
        //        LastBlockJson = JsonConvert.SerializeObject(block);
        //    }
        //}
        public static (BlockTypes type, string json) ParseBlock(TransactionBlock tx)
        {
            if (tx == null)
                return (BlockTypes.Null, null);

            return (tx.BlockType, JsonConvert.SerializeObject(tx));
        }
    }

    public abstract class DebiWorkflow
    {
        // submit block to consensus network
        // monitor timeout and return result 
        public Action<IWorkflowBuilder<LyraContext>> letConsensus => new Action<IWorkflowBuilder<LyraContext>>(branch => branch

                .If(data => data.GetLastBlock() != null).Do(then => then
                    .StartWith<CustomMessage>()
                        .Name("Log")
                        .Input(step => step.Message, data => $"Block is {data.LastBlockType} Let's consensus")
                        .Output(data => data.TimeTicks, step => DateTime.UtcNow.Ticks)
                        .Output(data => data.LastResult, step => null)
                    .Parallel()
                        .Do(then => then
                            .StartWith<SubmitBlock>()
                                .Input(step => step.block, data => data.GetLastBlock())
                            .Then<CustomMessage>()
                                .Name("Log")
                                .Input(step => step.Message, data => $"Block {data.GetLastBlock().Hash} submitted. Waiting for result...")
                            .WaitFor("MgBlkDone", data => data.SendHash, data => new DateTime(data.TimeTicks, DateTimeKind.Utc))
                                .Output(data => data.LastResult, step => step.EventData)
                            .Then<CustomMessage>()
                                .Name("Log")
                                .Input(step => step.Message, data => $"Consensus event is {data.LastResult}.")
                            )
                        .Do(then => then
                            .StartWith<CustomMessage>()
                                .Name("Log")
                                .Input(step => step.Message, data => $"Consensus is monitored.")
                            .Delay(data => TimeSpan.FromSeconds(15 + LyraGlobal.CONSENSUS_TIMEOUT * 3))
                            .Then<CustomMessage>()
                                .Name("Log")
                                .Input(step => step.Message, data => $"Consensus is timeout.")
                                .Output(data => data.LastResult, step => ConsensusResult.Uncertain)
                                .Output(data => data.State, step => WFState.ConsensusTimeout)
                            .Then<CustomMessage>()
                                .Name("Log")
                                .Input(step => step.Message, data => $"State Changed.")
                            )
                    .Join()
                        .CancelCondition(data => data.LastResult != null, true)
                    .Then<CustomMessage>()
                                .Name("Log")
                                .Input(step => step.Message, data => $"Consensus completed with {data.LastResult}")
                    )
                .If(data => data.GetLastBlock() == null).Do(then => then
                    .StartWith<CustomMessage>()
                        .Name("Log")
                        .Input(step => step.Message, data => $"Block is null. Terminate.")
                    .Output(data => data.State, step => WFState.Finished)
                    .Then<CustomMessage>()
                         .Name("Log")
                         .Input(step => step.Message, data => $"State Changed.")
                ));

        public void Build(IWorkflowBuilder<LyraContext> builder)
        {
            builder
                .StartWith(a =>
                {
                    a.Workflow.Reference = "start";
                    //Console.WriteLine($"{this.GetType().Name} start with {a.Workflow.Data}");
                    //ConsensusService.Singleton.LockIds(LockingIds);
                    return ExecutionResult.Next();
                })
                    .Output(data => data.State, step => WFState.Running)
                    .Then<CustomMessage>()
                        .Name("Log")
                        .Input(step => step.Message, data => $"State Changed.")
                    .While(a => a.State != WFState.Finished && a.State != WFState.Error)
                        .Do(x => x
                            .If(data => data.State == WFState.Init || data.State == WFState.Running)
                                .Do(then => then
                                .StartWith<Repeator>()      // WF to generate new block
                                    .Input(step => step.count, data => data.Count)
                                    .Output(data => data.Count, step => step.count)
                                    .Output(data => data.LastBlockType, step => LyraContext.ParseBlock(step.block).type)
                                    .Output(data => data.LastBlockJson, step => LyraContext.ParseBlock(step.block).json)
                                .If(data => data.LastBlockType != BlockTypes.Null).Do(letConsensus))    // send to consensus network
                            .If(data => data.LastBlockType != BlockTypes.Null && data.LastResult == ConsensusResult.Nay)
                                .Do(then => then
                                .StartWith<CustomMessage>()
                                    .Name("Log")
                                    .Input(step => step.Message, data => $"Consensus Nay. workflow failed.")
                                    .Output(data => data.State, step => WFState.Error)
                                .Then<CustomMessage>()
                                      .Name("Log")
                                      .Input(step => step.Message, data => $"State Changed.")
                                )
                            .If(data => data.LastBlockType != BlockTypes.Null && data.State == WFState.ConsensusTimeout)
                                .Do(then => then
                                .Parallel()
                                    .Do(then => then
                                        .StartWith<ReqViewChange>()
                                            .Output(data => data.State, step => step.PermanentFailed ? WFState.Error : WFState.Running)
                                        .WaitFor("ViewChanged", data => data.GetLastBlock().ServiceHash, data => DateTime.Now)
                                            .Output(data => data.LastResult, step => step.EventData)
                                            .Output(data => data.State, step => WFState.Running)
                                        .Then<CustomMessage>()
                                                .Name("Log")
                                                .Input(step => step.Message, data => $"View changed.")
                                        )
                                    .Do(then => then
                                        .StartWith<CustomMessage>()
                                            .Name("Log")
                                            .Input(step => step.Message, data => $"View change is monitored.")
                                        .Delay(data => TimeSpan.FromSeconds(LyraGlobal.VIEWCHANGE_TIMEOUT * 3))
                                        .Then<CustomMessage>()
                                            .Name("Log")
                                            .Input(step => step.Message, data => $"View change is timeout.")
                                        )
                                    .Join()
                                        .CancelCondition(data => data.State == WFState.Running, true)
                                )
                            ) // do
                .Then<CustomMessage>()
                    .Name("Log")
                    .Input(step => step.Message, data => $"Workflow is done.")
                .Then(a =>
                {
                    //Console.WriteLine("Ends.");
                    a.Workflow.Reference = "end";
                    //ConsensusService.Singleton.UnLockIds(LockingIds);
                })
                ;
        }
    }

    public class ReqViewChange : StepBodyAsync
    {
        private ILogger _logger;

        public bool PermanentFailed { get; set; }

        public ReqViewChange(ILogger<ReqViewChange> logger)
        {
            _logger = logger;
            PermanentFailed = false;
        }

        public override async Task<ExecutionResult> RunAsync(IStepExecutionContext context)
        {
            _logger.LogInformation($"WF Request View Change.");

            var ctx = context.Workflow.Data as LyraContext;

            ctx.ViewChangeReqCount++;
            if (ctx.ViewChangeReqCount > 10)
            {
                _logger.LogInformation($"View change req more than 10 times. Permanent error. Key: {ctx.SvcRequest}: {ctx.SendHash}");
                ctx.State = WFState.Error;
                PermanentFailed = true;
            }
            else
            {
                await ConsensusService.Singleton.BeginChangeViewAsync("WF Engine", ViewChangeReason.ConsensusTimeout);
            }

            return ExecutionResult.Next();
        }
    }

    public class SubmitBlock : StepBodyAsync
    {
        private ILogger _logger;

        public TransactionBlock? block { get; set; }

        public SubmitBlock(ILogger<SubmitBlock> logger)
        {
            _logger = logger;
        }

        public override async Task<ExecutionResult> RunAsync(IStepExecutionContext context)
        {
            var ctx = context.Workflow.Data as LyraContext;

            _logger.LogInformation($"In SubmitBlock: Leader? {ConsensusService.Singleton.IsThisNodeLeader} {block.BlockType} {block.Hash} {ctx.State}");

            if (ConsensusService.Singleton.IsThisNodeLeader)
                await ConsensusService.Singleton.LeaderSendBlockToConsensusAndForgetAsync(block);

            return ExecutionResult.Next();
        }
    }

    public class CustomMessage : StepBodyAsync
    {
        public string Message { get; set; }

        private ILogger _logger;

        public CustomMessage(ILogger<CustomMessage> logger)
        {
            _logger = logger;
        }

        public override async Task<ExecutionResult> RunAsync(IStepExecutionContext context)
        {
            var ctx = context.Workflow.Data as LyraContext;
            var log = $"([WF] {DateTime.Now:mm:ss.ff}) Key is: {ctx.SendHash}, {ctx.Count}/{ctx.State}, {Message}";
            _logger.LogInformation(log);

            await ConsensusService.Singleton.FireSignalrWorkflowEventAsync(new WorkflowEvent
            {
                Owner = ctx.OwnerAccountId,
                State = Message == "Workflow is done." ? "Exited" : ctx.State.ToString(),
                Name = ctx.SvcRequest,
                Key = ctx.SendHash,
                Action = ctx.LastBlockType.ToString(),
                Result = ctx.LastResult.ToString(),
                Message = Message,
            });
            return ExecutionResult.Next();
        }
    }
}
