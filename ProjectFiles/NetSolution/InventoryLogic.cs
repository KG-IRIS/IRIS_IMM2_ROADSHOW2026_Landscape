#region Using directives
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Newtonsoft.Json;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.CoreBase;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.UI;
using FTOptix.NativeUI;
using FTOptix.Core;
using FTOptix.RAEtherNetIP;
using FTOptix.OPCUAServer;
#endregion

public class InventoryLogic : BaseNetLogic
{
    private const string PlexURL = "https://test.connect.plex.com";

    private PeriodicTask _periodicTask;

    public override void Start()
    {
        _periodicTask = new PeriodicTask(FetchAndDisplay, 10000, LogicObject);
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
            string apiKey = GetApiKey();
            if (apiKey == null) return;

            string workcenterName = GetWorkcenterName();
            if (workcenterName == null) return;

            string json = GetRequest(apiKey);
            if (string.IsNullOrEmpty(json))
            {
                Log.Warning("InventoryLogic", "Empty response from Plex.");
                return;
            }

            var list = JsonConvert.DeserializeObject<List<PlexContainerDto>>(json);
            if (list == null || list.Count == 0)
            {
                Log.Warning("InventoryLogic", "Deserialization returned empty list.");
                return;
            }

            var matched = list
                .Where(c => string.Equals(c.location, workcenterName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matched.Count == 0)
                Log.Warning("InventoryLogic", $"No containers found at location '{workcenterName}'.");

            PopulateModelVariables("Model/Plex/Inventory1", matched.Count > 0 ? matched[0] : null);
            PopulateModelVariables("Model/Plex/Inventory2", matched.Count > 1 ? matched[1] : null);
        }
        catch (Exception ex)
        {
            Log.Error("InventoryLogic", $"FetchAndDisplay failed: {ex.Message}");
        }
    }

    private void PopulateModelVariables(string folderPath, PlexContainerDto container)
    {
        try
        {
            var folder = GetFolder(folderPath);
            if (folder == null) return;

            if (container == null)
            {
                SetModelVar(folder, "SerialNo", "—");
                SetModelVar(folder, "PartNo", "—");
                SetModelVar(folder, "Part Name", "—");
                SetModelVar(folder, "Operation Code", "—");
                SetModelVar(folder, "Location", "—");
                SetModelVar(folder, "Quantity", "—");
                SetModelVar(folder, "Units", "—");
                SetModelVar(folder, "Net Weight", "—");
                SetModelVar(folder, "Container Type", "—");
                SetModelVar(folder, "LotNo", "—");
                SetModelVar(folder, "Inventory Type", "—");
                return;
            }

            SetModelVar(folder, "SerialNo", container.serialNo);
            SetModelVar(folder, "PartNo", container.partNo);
            SetModelVar(folder, "Part Name", container.partName);
            SetModelVar(folder, "Operation Code", container.operationCode);
            SetModelVar(folder, "Location", container.location);
            SetModelVar(folder, "Quantity", container.quantity?.ToString("F0"));
            SetModelVar(folder, "Units", container.quantityInventoryUnit);
            SetModelVar(folder, "Net Weight", container.netWeight?.ToString("F2"));
            SetModelVar(folder, "Container Type", container.containerType);
            SetModelVar(folder, "LotNo", container.lotNo);
            SetModelVar(folder, "Inventory Type", container.inventoryType);
        }
        catch (Exception ex)
        {
            Log.Error("InventoryLogic", $"PopulateModelVariables({folderPath}) failed: {ex.Message}");
        }
    }

    private IUANode GetFolder(string folderPath)
    {
        var folder = Project.Current.Get(folderPath);
        if (folder == null)
            Log.Warning("InventoryLogic", $"Could not find '{folderPath}' folder.");
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
                Log.Warning("InventoryLogic", $"Model variable '{variableName}' not found in folder.");
                return;
            }
            variable.Value = value ?? string.Empty;
        }
        catch (Exception ex)
        {
            Log.Error("InventoryLogic", $"SetModelVar({variableName}) failed: {ex.Message}");
        }
    }

    private IUANode ResolveWorkcenterNode()
    {
        var aliasNode = Project.Current.Get("Model/HostSettings/PlexWorkcenter");
        if (aliasNode == null)
        {
            Log.Error("InventoryLogic", "Could not find 'Model/HostSettings/PlexWorkcenter'.");
            return null;
        }

        var nodeIdVar = aliasNode.GetVariable("PlexWorkcenter");
        if (nodeIdVar == null)
        {
            Log.Error("InventoryLogic", "Could not find NodeId variable on PlexWorkcenter alias.");
            return null;
        }

        var targetNodeId = (UAManagedCore.NodeId)nodeIdVar.Value;
        if (targetNodeId == null)
        {
            Log.Error("InventoryLogic", "PlexWorkcenter NodeId value is null.");
            return null;
        }

        var plexWCNode = InformationModel.Get(targetNodeId);
        if (plexWCNode == null)
            Log.Error("InventoryLogic", $"Could not resolve node from NodeId '{targetNodeId}'.");

        return plexWCNode;
    }

    private string GetWorkcenterName()
    {
        var node = ResolveWorkcenterNode();
        if (node == null) return null;

        var nameVar = node.GetVariable("WorkcenterName");
        if (nameVar == null)
        {
            Log.Error("InventoryLogic", $"WorkcenterName not found on '{node.BrowseName}'.");
            return null;
        }

        string workcenterName = nameVar.Value?.Value?.ToString();
        if (string.IsNullOrWhiteSpace(workcenterName))
        {
            Log.Warning("InventoryLogic", "WorkcenterName is empty.");
            return null;
        }

        return workcenterName;
    }

    private string GetApiKey()
    {
        var apiKeyVar = LogicObject.GetVariable("apiKey");
        if (apiKeyVar == null)
        {
            Log.Error("InventoryLogic", "Variable 'apiKey' not found on LogicObject.");
            return null;
        }

        string apiKey = apiKeyVar.Value?.Value?.ToString();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Log.Warning("InventoryLogic", "apiKey is empty.");
            return null;
        }

        return apiKey;
    }

    private string GetRequest(string apiKey)
    {
        try
        {
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.Add("X-Plex-Connect-Api-Key", apiKey);

                var url = $"{PlexURL}/inventory/v1/inventory-tracking/containers";
                var response = client.GetAsync(url).Result;
                response.EnsureSuccessStatusCode();
                return response.Content.ReadAsStringAsync().Result;
            }
        }
        catch (Exception ex)
        {
            Log.Error("InventoryLogic", $"HTTP request failed: {ex.Message}");
            return null;
        }
    }

    private class PlexContainerDto
    {
        public string serialNo { get; set; }
        public string partId { get; set; }
        public string partNo { get; set; }
        public string revision { get; set; }
        public string partNoRevision { get; set; }
        public string partName { get; set; }
        public string partOperationId { get; set; }
        public int? operationNo { get; set; }
        public string operationCode { get; set; }
        public string containerStatus { get; set; }
        public string locationId { get; set; }
        public string location { get; set; }
        public decimal? quantity { get; set; }
        public string quantityInventoryUnit { get; set; }
        public decimal? grossWeight { get; set; }
        public decimal? netWeight { get; set; }
        public decimal? tareWeight { get; set; }
        public string containerType { get; set; }
        public string trackingNo { get; set; }
        public string addDateTime { get; set; }
        public string updateDateTime { get; set; }
        public string containerShelfDateTime { get; set; }
        public string masterUnitId { get; set; }
        public string masterUnitNo { get; set; }
        public string lotId { get; set; }
        public string lotNo { get; set; }
        public string heatId { get; set; }
        public string heatCode { get; set; }
        public string heatNo { get; set; }
        public string eun { get; set; }
        public string inventoryType { get; set; }
    }
}
