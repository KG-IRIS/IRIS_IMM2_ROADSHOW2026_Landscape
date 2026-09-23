#region Using directives
using System;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.WebUI;
using FTOptix.NetLogic;
using FTOptix.NativeUI;
using FTOptix.CoreBase;
using FTOptix.Alarm;
using FTOptix.Recipe;
using FTOptix.EventLogger;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.Retentivity;
using FTOptix.MQTTClient;
using FTOptix.DataLogger;
using FTOptix.Core;
using FTOptix.RAEtherNetIP;
using FTOptix.OPCUAServer;
#endregion

public class WorkcenterManager : BaseNetLogic
{
    private const string PlexURL = "https://test.connect.plex.com";

    private const string AccountId = "82a19553-19a4-45b4-8818-741916727fd1";

    private const string StatusIdle = "5ab64e92-48e6-4ef7-9ea1-59f32c5ecd9e";
    private const string StatusProduction = "41f1c708-f393-4dac-a3f8-fa582d42ab9b";
    private const string StatusOff = "0e5b2fee-aeb9-45c4-9e68-e6633605e939";

    private IUAVariable _machineStatusVar;

    public override void Start()
    {
        try
        {
            _machineStatusVar = Project.Current.GetVariable("Model/InjectionMoldingMachine/MachineStatus");
            if (_machineStatusVar == null)
            {
                Log.Error("PlexAPIs", "Variable 'Model/InjectionMoldingMachine/MachineStatus' not found.");
                return;
            }

            _machineStatusVar.VariableChange += MachineStatus_VariableChange;
        }
        catch (Exception ex)
        {
            Log.Error("PlexAPIs", $"Start failed: {ex.Message}");
        }
    }

    public override void Stop()
    {
        try
        {
            if (_machineStatusVar != null)
                _machineStatusVar.VariableChange -= MachineStatus_VariableChange;
        }
        catch (Exception ex)
        {
            Log.Error("PlexAPIs", $"Stop failed: {ex.Message}");
        }
    }

    private void MachineStatus_VariableChange(object sender, VariableChangeEventArgs e)
    {
        try
        {
            int newStatus = Convert.ToInt32(e.NewValue?.Value ?? 0);

            string statusId = MapMachineStatusToStatusId(newStatus);
            if (statusId == null)
            {
                Log.Warning("PlexAPIs", $"Unknown MachineStatus value: {newStatus}. No status change sent.");
                return;
            }

            string apiKey = GetApiKey();
            if (apiKey == null) return;

            string workcenterId = GetWorkcenterId();
            if (workcenterId == null) return;

            string workcenterLabel = GetWorkcenterLabel();

            SetWorkcenterStatus(apiKey, workcenterId, statusId, workcenterLabel);
        }
        catch (Exception ex)
        {
            Log.Error("PlexAPIs", $"MachineStatus_VariableChange failed: {ex.Message}");
        }
    }

    private IUANode ResolveWorkcenterNode()
    {
        var aliasNode = Project.Current.Get("Model/HostSettings/PlexWorkcenter");
        if (aliasNode == null)
        {
            Log.Error("PlexAPIs", "Could not find 'Model/HostSettings/PlexWorkcenter'.");
            return null;
        }

        var nodeIdVar = aliasNode.GetVariable("PlexWorkcenter");
        if (nodeIdVar == null)
        {
            Log.Error("PlexAPIs", "Could not find NodeId variable on PlexWorkcenter alias.");
            return null;
        }

        var targetNodeId = (UAManagedCore.NodeId)nodeIdVar.Value;
        if (targetNodeId == null)
        {
            Log.Error("PlexAPIs", "PlexWorkcenter NodeId value is null.");
            return null;
        }

        var plexWCNode = InformationModel.Get(targetNodeId);
        if (plexWCNode == null)
            Log.Error("PlexAPIs", $"Could not resolve node from NodeId '{targetNodeId}'.");

        return plexWCNode;
    }

    private string GetWorkcenterId()
    {
        var node = ResolveWorkcenterNode();
        if (node == null) return null;

        var idVar = node.GetVariable("WorkcenterId");
        if (idVar == null)
        {
            Log.Error("PlexAPIs", $"WorkcenterId not found on '{node.BrowseName}'.");
            return null;
        }

        string workcenterId = idVar.Value?.Value?.ToString();
        if (string.IsNullOrWhiteSpace(workcenterId))
        {
            Log.Warning("PlexAPIs", "WorkcenterId is empty.");
            return null;
        }

        return workcenterId;
    }

    private string GetWorkcenterLabel()
    {
        var node = ResolveWorkcenterNode();
        return node?.BrowseName ?? "Unknown";
    }


    private string MapMachineStatusToStatusId(int machineStatus)
    {
        switch (machineStatus)
        {
            case 0: return StatusIdle;
            case 1: return StatusProduction;
            case 2: return StatusOff;
            default: return null;
        }
    }

    private void SetWorkcenterStatus(string apiKey, string workcenterId, string statusId, string label)
    {
        try
        {
            var body = new SetStatusRequest
            {
                workcenterStatusId = statusId,
                workcenterEventId = "",
                accountId = AccountId
            };

            string json = Post(
                apiKey,
                $"/production/v1/control/workcenters/{workcenterId}/status",
                JsonConvert.SerializeObject(body));

            if (json == null)
                Log.Warning("PlexAPIs", $"{label}: status update failed.");
        }
        catch (Exception ex)
        {
            Log.Error("PlexAPIs", $"SetWorkcenterStatus({label}) failed: {ex.Message}");
        }
    }

    // --- API key helper ---

    private string GetApiKey()
    {
        var apiKeyVar = LogicObject.GetVariable("apiKey");
        if (apiKeyVar == null)
        {
            Log.Error("PlexAPIs", "Variable 'apiKey' not found on LogicObject.");
            return null;
        }

        string apiKey = apiKeyVar.Value?.Value?.ToString();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Log.Warning("PlexAPIs", "apiKey is empty.");
            return null;
        }

        return apiKey;
    }

    // --- HTTP helper ---

    private string Post(string apiKey, string endpoint, string jsonBody)
    {
        try
        {
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.Add("X-Plex-Connect-Api-Key", apiKey);

                var content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
                var response = client.PostAsync(PlexURL + endpoint, content).Result;
                string responseBody = response.Content.ReadAsStringAsync().Result;

                if (!response.IsSuccessStatusCode)
                {
                    Log.Error("PlexAPIs", $"POST {endpoint} returned {(int)response.StatusCode}: {responseBody}");
                    return null;
                }

                return responseBody;
            }
        }
        catch (Exception ex)
        {
            Log.Error("PlexAPIs", $"HTTP POST {endpoint} failed: {ex.Message}");
            return null;
        }
    }

    // --- DTO ---

    private class SetStatusRequest
    {
        public string workcenterStatusId { get; set; }
        public string workcenterEventId { get; set; }
        public string accountId { get; set; }
    }
}
