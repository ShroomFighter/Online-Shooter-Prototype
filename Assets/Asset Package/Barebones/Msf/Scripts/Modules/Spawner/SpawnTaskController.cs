﻿using Barebones.Logging;
using Barebones.Networking;
using System;
using System.Collections.Generic;

namespace Barebones.MasterServer
{
    public class SpawnTaskController
    {
        private readonly IClientSocket _connection;
        public int SpawnId { get; private set; }
        public Dictionary<string, string> Properties { get; private set; }

        public SpawnTaskController(int spawnId, Dictionary<string, string> properties, IClientSocket connection)
        {
            _connection = connection;
            SpawnId = spawnId;
            Properties = properties;
        }

        public void FinalizeTask()
        {
            FinalizeTask(new Dictionary<string, string>(), () => { });
        }

        public void FinalizeTask(Dictionary<string, string> finalizationData)
        {
            FinalizeTask(finalizationData, () => { });
        }

        public void FinalizeTask(Dictionary<string, string> finalizationData, Action callback)
        {
            Msf.Server.Spawners.FinalizeSpawnedProcess(SpawnId, finalizationData, (successful, error) =>
            {
                if (error != null)
                {
                    Logs.Error("Error while completing the spawn task: " + error);
                }

                callback.Invoke();
            }, _connection);
        }
    }
}