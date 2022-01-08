﻿using Lyra.Core.Blocks;
using Lyra.Data.Blocks;
using MongoDB.Bson.Serialization.Attributes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Lyra.Data.API.WorkFlow
{
    public enum OtcOrderStatus { Open, Partial, Closed, Dispute };
    public interface IOtcOrder : IBrokerAccount
    {
        OTCOrder Order { get; set; }
        OtcOrderStatus Status { get; set; }
    }

    [BsonIgnoreExtraElements]
    public class OtcOrderRecvBlock : BrokerAccountRecv, IOtcOrder
    {
        public OTCOrder Order { get; set; }
        public OtcOrderStatus Status { get; set; }

        public override BlockTypes GetBlockType()
        {
            return BlockTypes.OTCOrderRecv;
        }

        public override bool AuthCompare(Block other)
        {
            var ob = other as OtcOrderRecvBlock;

            return base.AuthCompare(ob) &&
                    Order.Equals(ob.Order) &&
                    Status.Equals(ob.Status);
        }

        protected override string GetExtraData()
        {
            string extraData = base.GetExtraData();
            extraData += Order.GetExtraData() + "|";
            extraData += $"{Status}|";
            return extraData;
        }

        public override string Print()
        {
            string result = base.Print();
            result += $"{Order}\n";
            result += $"Status: {Status}\n";
            return result;
        }
    }

    [BsonIgnoreExtraElements]
    public class OtcOrderSendBlock : BrokerAccountSend, IOtcOrder
    {
        public OTCOrder Order { get; set; }
        public OtcOrderStatus Status { get; set; }

        public override BlockTypes GetBlockType()
        {
            return BlockTypes.OTCOrderSend;
        }

        public override bool AuthCompare(Block other)
        {
            var ob = other as OtcOrderSendBlock;

            return base.AuthCompare(ob) &&
                    Order.Equals(ob.Order) &&
                    Status.Equals(ob.Status);
        }

        protected override string GetExtraData()
        {
            string extraData = base.GetExtraData();
            extraData += Order.GetExtraData() + "|";
            extraData += $"{Status}|";
            return extraData;
        }

        public override string Print()
        {
            string result = base.Print();
            result += $"{Order}\n";
            result += $"Status: {Status}\n";
            return result;
        }
    }


    [BsonIgnoreExtraElements]
    public class OtcOrderGenesis : OtcOrderRecvBlock, IOpeningBlock
    {
        public AccountTypes AccountType { get; set; }

        public override BlockTypes GetBlockType()
        {
            return BlockTypes.OTCOrderGenesis;
        }

        public override bool AuthCompare(Block other)
        {
            var ob = other as OtcOrderGenesis;

            return base.AuthCompare(ob) &&
                AccountType == ob.AccountType
                ;
        }

        protected override string GetExtraData()
        {
            string extraData = base.GetExtraData();
            extraData += AccountType + "|";
            return extraData;
        }

        public override string Print()
        {
            string result = base.Print();
            result += $"AccountType: {AccountType}\n";
            return result;
        }
    }
}
