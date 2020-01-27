﻿using Lyra.Core.Decentralize;
using System;
using System.Collections.Generic;
using System.Text;

namespace LyraNodesBot
{
    class BillBoardData
    {
        public Dictionary<string, PosNodeData> AllNodes { get; set; }
        public bool canDoConsensus { get; set; }
    }

    public class PosNodeData
    {
        public string accountID { get; set; }
        public string ip { get; set; }
        public decimal balance { get; set; }
        public DateTime lastStaking { get; set; }
        public string netStatus { get; set; }
        public bool ableToAuthorize { get; set; }

        public IEnumerable<string> FailReasons
        {
            get
            {
                if (balance < 1000000)
                    yield return "Not enough balance";
                if (DateTime.Now - lastStaking > TimeSpan.FromMinutes(12))
                    yield return "Not active for a while";
                if (netStatus != "FulllyConnected")
                    yield return "Not fully connected: " + netStatus;
            }
        }
    }





}
