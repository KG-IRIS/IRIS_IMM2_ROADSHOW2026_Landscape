#region Using directives
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Newtonsoft.Json;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.HMIProject;
using FTOptix.UI;
using FTOptix.NativeUI;
using FTOptix.Core;
using FTOptix.CoreBase;
using FTOptix.NetLogic;
using FTOptix.RAEtherNetIP;
using FTOptix.OPCUAServer;
#endregion

public class WorkcenterLogic : BaseNetLogic
{
    private const string PlexURL = "https://test.connect.plex.com";

    private PeriodicTask _periodicTask;

    public override void Start()
    {
        _periodicTask = new PeriodicTask(FetchAndDisplay, 4000, LogicObject);
        _periodicTask.Start();
    }

    public override void Stop()
    {
        _periodicTask?.Dispose();
    }

    private void FetchAndDisplay()
    {
        try
        {
            var apiKeyVar = LogicObject.GetVariable("apiKey");
            if (apiKeyVar == null)
            {
                Log.Error("WorkcenterLogic", "Variable 'apiKey' not found on Owner.");
                return;
            }

            string apiKey = apiKeyVar.Value?.Value?.ToString();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Log.Warning("WorkcenterLogic", "apiKey is empty.");
                return;
            }

            var aliasNode = Project.Current.Get("Model/HostSettings/PlexWorkcenter");
            if (aliasNode == null)
            {
                Log.Error("WorkcenterLogic", "Could not find 'Model/HostSettings/PlexWorkcenter'.");
                return;
            }

            var nodeIdVar = aliasNode.GetVariable("PlexWorkcenter");
            if (nodeIdVar == null)
            {
                Log.Error("WorkcenterLogic", "Could not find NodeId variable on PlexWorkcenter alias.");
                return;
            }

            var targetNodeId = (UAManagedCore.NodeId)nodeIdVar.Value;
            if (targetNodeId == null)
            {
                Log.Error("WorkcenterLogic", "PlexWorkcenter NodeId value is null.");
                return;
            }

            var plexWCNode = InformationModel.Get(targetNodeId);
            if (plexWCNode == null)
            {
                Log.Error("WorkcenterLogic", $"Could not resolve node from NodeId '{targetNodeId}'.");
                return;
            }

            var nameVar = plexWCNode.GetVariable("WorkcenterName");
            if (nameVar == null)
            {
                Log.Error("WorkcenterLogic", $"WorkcenterName not found on '{plexWCNode.BrowseName}'.");
                return;
            }

            string workcenterName = nameVar.Value?.Value?.ToString();
            if (string.IsNullOrWhiteSpace(workcenterName))
            {
                Log.Warning("WorkcenterLogic", "WorkcenterName is empty.");
                return;
            }

            string json = GetRequest(apiKey, "/production/v1/production-definitions/approved-workcenters");
            if (string.IsNullOrEmpty(json))
            {
                Log.Warning("WorkcenterLogic", "Empty response from approved-workcenters endpoint.");
                return;
            }

            var list = JsonConvert.DeserializeObject<List<PlexWorkcenterDto>>(json);
            if (list == null || list.Count == 0)
            {
                Log.Warning("WorkcenterLogic", "Deserialization returned empty list.");
                return;
            }

            var wc = list.FirstOrDefault(w =>
                string.Equals(w.workcenterName, workcenterName, StringComparison.OrdinalIgnoreCase));

            if (wc == null)
            {
                Log.Warning("WorkcenterLogic", $"Workcenter '{workcenterName}' not found in API response.");
                return;
            }

            string status = FetchWorkcenterStatus(apiKey, wc.workcenterId);

            PopulateModelVariables(wc, status);
        }
        catch (Exception ex)
        {
            Log.Error("WorkcenterLogic", $"FetchAndDisplay failed: {ex.Message}");
        }
    }

    private void PopulateModelVariables(PlexWorkcenterDto wc, string status)
    {
        try
        {
            var folder = Project.Current.Get("Model/Plex/Workcenters");
            if (folder == null)
            {
                Log.Warning("WorkcenterLogic", "Could not find 'Model/Plex/Workcenters' folder.");
                return;
            }

            if (wc == null)
            {
                SetModelVar(folder, "Name", "—");
                SetModelVar(folder, "PartNo", "—");
                SetModelVar(folder, "OperationNo", "—");
                SetModelVar(folder, "Crew Size", "—");
                SetModelVar(folder, "Operation Code", "—");
                SetModelVar(folder, "Standard Rate", "—");
                SetModelVar(folder, "Setup Time", "—");
                SetModelVar(folder, "Ideal Rate", "—");
                SetModelVar(folder, "Target Rate", "—");
                SetModelVar(folder, "Status", "—");
                return;
            }

            SetModelVar(folder, "Name", wc.workcenterName);
            SetModelVar(folder, "PartNo", wc.partNo);
            SetModelVar(folder, "OperationNo", wc.operationNo?.ToString());
            SetModelVar(folder, "Crew Size", wc.crewSize?.ToString("F2"));
            SetModelVar(folder, "Operation Code", wc.operationCode);
            SetModelVar(folder, "Standard Rate", wc.standardProductionRate?.ToString("F2"));
            SetModelVar(folder, "Setup Time", wc.setupTime?.ToString("F2"));
            SetModelVar(folder, "Ideal Rate", wc.idealRate?.ToString("F2"));
            SetModelVar(folder, "Target Rate", wc.targetRate?.ToString("F2"));
            SetModelVar(folder, "Status", status ?? "—");
        }
        catch (Exception ex)
        {
            Log.Error("WorkcenterLogic", $"PopulateModelVariables failed: {ex.Message}");
        }
    }

    private void SetModelVar(IUANode folder, string variableName, string value)
    {
        try
        {
            var variable = folder.GetVariable(variableName);
            if (variable == null)
            {
                Log.Warning("WorkcenterLogic", $"Model variable '{variableName}' not found in 'Model/Plex/Workcenters'.");
                return;
            }
            variable.Value = value ?? string.Empty;
        }
        catch (Exception ex)
        {
            Log.Error("WorkcenterLogic", $"SetModelVar({variableName}) failed: {ex.Message}");
        }
    }

    private string FetchWorkcenterStatus(string apiKey, string workcenterId)
    {
        try
        {
            string json = GetRequest(apiKey, $"/production/v1-beta1/control/workcenters/{workcenterId}");
            if (string.IsNullOrEmpty(json))
            {
                Log.Warning("WorkcenterLogic", $"Empty status response for workcenter {workcenterId}.");
                return null;
            }

            var setup = JsonConvert.DeserializeObject<WorkcenterSetupDto>(json);
            if (setup == null)
            {
                Log.Warning("WorkcenterLogic", $"Failed to parse status for workcenter {workcenterId}.");
                return null;
            }

            return setup.statusDescription;
        }
        catch (Exception ex)
        {
            Log.Error("WorkcenterLogic", $"FetchWorkcenterStatus({workcenterId}) failed: {ex.Message}");
            return null;
        }
    }

    private string GetRequest(string apiKey, string endpoint)
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
            Log.Error("WorkcenterLogic", $"HTTP request to {endpoint} failed: {ex.Message}");
            return null;
        }
    }

    private class PlexWorkcenterDto
    {
        public string workcenterId { get; set; }
        public string workcenterCode { get; set; }
        public string workcenterName { get; set; }
        public string partId { get; set; }
        public string partNo { get; set; }
        public string partRevision { get; set; }
        public string partNoRevision { get; set; }
        public string partOperationId { get; set; }
        public string operationId { get; set; }
        public int? operationNo { get; set; }
        public string operationCode { get; set; }
        public decimal? crewSize { get; set; }
        public decimal? standardProductionRate { get; set; }
        public string note { get; set; }
        public decimal? setupTime { get; set; }
        public decimal? idealRate { get; set; }
        public decimal? targetRate { get; set; }
        public decimal? setupCrewSize { get; set; }
        public decimal? sequenceRate { get; set; }
    }

    private class WorkcenterSetupDto
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
        public object operators { get; set; }
    }
}
