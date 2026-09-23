#region Using directives
using System;
using System.Collections.Generic;
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

public class OperatorLogic : BaseNetLogic
{
    private const string PlexURL = "https://test.connect.plex.com";
    private const string AccountId = "82a19553-19a4-45b4-8818-741916727fd1";
    private const string CostSubTypeId = "e4b36350-f977-41bc-bcf1-570e7c171795";

    private DelayedTask _initTask;

    public override void Start()
    {
        _initTask = new DelayedTask(FetchAndDisplay, 500, LogicObject);
        _initTask.Start();
    }

    public override void Stop()
    {
        _initTask?.Dispose();
    }

    private void FetchAndDisplay()
    {
        try
        {
            string apiKey = GetApiKey();
            if (apiKey == null) return;

            string workcenterId = GetWorkcenterId();
            if (workcenterId == null) return;

            string json = Get(apiKey, $"/production/v1-beta1/control/workcenters/{workcenterId}");
            if (string.IsNullOrEmpty(json))
            {
                Log.Warning("OperatorLogic", "Empty response from Plex.");
                return;
            }

            var dto = JsonConvert.DeserializeObject<WorkcenterOperatorsDto>(json);
            if (dto == null)
            {
                Log.Warning("OperatorLogic", "Deserialization returned null.");
                return;
            }

            PopulateModelVariables(dto);
        }
        catch (Exception ex)
        {
            Log.Error("OperatorLogic", $"FetchAndDisplay failed: {ex.Message}");
        }
    }

    [ExportMethod]
    public void ClockIn()
    {
        try
        {
            string apiKey = GetApiKey();
            if (apiKey == null) return;

            string workcenterId = GetWorkcenterId();
            if (workcenterId == null) return;

            var body = new ClockInRequest
            {
                accountId = AccountId,
                costSubTypeId = CostSubTypeId
            };

            string json = Post(apiKey, $"/production/v1/control/workcenters/{workcenterId}/operators/clockin", JsonConvert.SerializeObject(body));
            if (!string.IsNullOrEmpty(json))
            {
                var dto = JsonConvert.DeserializeObject<WorkcenterOperatorsDto>(json);
                PopulateModelVariables(dto);
            }
            else
            {
                Log.Warning("OperatorLogic", "ClockIn: empty response.");
            }
        }
        catch (Exception ex)
        {
            Log.Error("OperatorLogic", $"ClockIn failed: {ex.Message}");
        }
    }

    [ExportMethod]
    public void ClockOut()
    {
        try
        {
            string apiKey = GetApiKey();
            if (apiKey == null) return;

            string workcenterId = GetWorkcenterId();
            if (workcenterId == null) return;

            var body = new ClockOutRequest { accountId = AccountId };
            Post(apiKey, $"/production/v1/control/workcenters/{workcenterId}/operators/clockout", JsonConvert.SerializeObject(body));

            SetModelVar(GetFolder(), "User", "—");
        }
        catch (Exception ex)
        {
            Log.Error("OperatorLogic", $"ClockOut failed: {ex.Message}");
        }
    }

    private void PopulateModelVariables(WorkcenterOperatorsDto dto)
    {
        try
        {
            var folder = GetFolder();
            if (folder == null) return;

            if (dto?.operators == null || dto.operators.Count == 0)
            {
                SetModelVar(folder, "User", "—");
                return;
            }

            SetModelVar(folder, "User", dto.operators[0].employeeName);
        }
        catch (Exception ex)
        {
            Log.Error("OperatorLogic", $"PopulateModelVariables failed: {ex.Message}");
        }
    }

    private IUANode GetFolder()
    {
        var folder = Project.Current.Get("Model/Plex/Operator");
        if (folder == null)
            Log.Warning("OperatorLogic", "Could not find 'Model/Plex/Operator' folder.");
        return folder;
    }

    private void SetModelVar(IUANode folder, string variableName, string value)
    {
        try
        {
            if (folder == null) return;

            var variable = folder.GetVariable(variableName);
            if (variable == null)
            {
                Log.Warning("OperatorLogic", $"Model variable '{variableName}' not found in 'Model/Plex/Operator'.");
                return;
            }
            variable.Value = value ?? string.Empty;
        }
        catch (Exception ex)
        {
            Log.Error("OperatorLogic", $"SetModelVar({variableName}) failed: {ex.Message}");
        }
    }

    private IUANode ResolveWorkcenterNode()
    {
        var aliasNode = Project.Current.Get("Model/HostSettings/PlexWorkcenter");
        if (aliasNode == null)
        {
            Log.Error("OperatorLogic", "Could not find 'Model/HostSettings/PlexWorkcenter'.");
            return null;
        }

        var nodeIdVar = aliasNode.GetVariable("PlexWorkcenter");
        if (nodeIdVar == null)
        {
            Log.Error("OperatorLogic", "Could not find NodeId variable on PlexWorkcenter alias.");
            return null;
        }

        var targetNodeId = (UAManagedCore.NodeId)nodeIdVar.Value;
        if (targetNodeId == null)
        {
            Log.Error("OperatorLogic", "PlexWorkcenter NodeId value is null.");
            return null;
        }

        var plexWCNode = InformationModel.Get(targetNodeId);
        if (plexWCNode == null)
            Log.Error("OperatorLogic", $"Could not resolve node from NodeId '{targetNodeId}'.");

        return plexWCNode;
    }

    private string GetWorkcenterId()
    {
        var node = ResolveWorkcenterNode();
        if (node == null) return null;

        var idVar = node.GetVariable("WorkcenterId");
        if (idVar == null)
        {
            Log.Error("OperatorLogic", $"WorkcenterId not found on '{node.BrowseName}'.");
            return null;
        }

        string workcenterId = idVar.Value?.Value?.ToString();
        if (string.IsNullOrWhiteSpace(workcenterId))
        {
            Log.Warning("OperatorLogic", "WorkcenterId is empty.");
            return null;
        }

        return workcenterId;
    }

    private string GetApiKey()
    {
        var apiKeyVar = LogicObject.GetVariable("apiKey");
        if (apiKeyVar == null)
        {
            Log.Error("OperatorLogic", "Variable 'apiKey' not found on LogicObject.");
            return null;
        }

        string apiKey = apiKeyVar.Value?.Value?.ToString();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Log.Warning("OperatorLogic", "apiKey is empty.");
            return null;
        }

        return apiKey;
    }

    private string Get(string apiKey, string endpoint)
    {
        try
        {
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.Add("X-Plex-Connect-Api-Key", apiKey);
                var response = client.GetAsync(PlexURL + endpoint).Result;
                response.EnsureSuccessStatusCode();
                return response.Content.ReadAsStringAsync().Result;
            }
        }
        catch (Exception ex)
        {
            Log.Error("OperatorLogic", $"HTTP GET {endpoint} failed: {ex.Message}");
            return null;
        }
    }

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
                    Log.Error("OperatorLogic", $"POST {endpoint} returned {(int)response.StatusCode}: {responseBody}");
                    return null;
                }

                return responseBody;
            }
        }
        catch (Exception ex)
        {
            Log.Error("OperatorLogic", $"HTTP POST {endpoint} failed: {ex.Message}");
            return null;
        }
    }

    private class ClockInRequest
    {
        public string accountId { get; set; }
        public string costSubTypeId { get; set; }
    }

    private class ClockOutRequest
    {
        public string accountId { get; set; }
    }

    private class WorkcenterOperatorsDto
    {
        public string id { get; set; }
        public string code { get; set; }
        public string name { get; set; }
        public string type { get; set; }
        public string statusId { get; set; }
        public string statusDescription { get; set; }
        public string jobId { get; set; }
        public string jobNumber { get; set; }
        public string jobOperationId { get; set; }
        public decimal? operationNumber { get; set; }
        public string operationCode { get; set; }
        public decimal? jobOperationQuantity { get; set; }
        public string lotId { get; set; }
        public string productionLineId { get; set; }
        public object batches { get; set; }
        public List<OperatorEntry> operators { get; set; }
    }

    private class OperatorEntry
    {
        public string accountID { get; set; }
        public string employeeName { get; set; }
        public string costSubTypeId { get; set; }
    }
}
