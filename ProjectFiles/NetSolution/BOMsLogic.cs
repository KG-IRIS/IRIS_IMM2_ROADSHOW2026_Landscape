#region Using directives
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
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

public class BOMsLogic : BaseNetLogic
{
    private const string PlexURL = "https://test.connect.plex.com";
    private const string PartId = "915e9a4a-0a4d-4947-81b1-9d5af23fa991";

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

            string json = GetRequest(apiKey);
            if (string.IsNullOrEmpty(json))
            {
                Log.Warning("BOMsLogic", "Empty response from Plex.");
                return;
            }

            var list = JsonConvert.DeserializeObject<List<PlexBomDto>>(json);
            if (list == null || list.Count == 0)
            {
                Log.Warning("BOMsLogic", "Deserialization returned empty list.");
                return;
            }

            PopulateModelVariables("Model/Plex/BOMs1", list.ElementAtOrDefault(0));
            PopulateModelVariables("Model/Plex/BOMs2", list.ElementAtOrDefault(1));
        }
        catch (Exception ex)
        {
            Log.Error("BOMsLogic", $"FetchAndDisplay failed: {ex.Message}");
        }
    }

    private void PopulateModelVariables(string folderPath, PlexBomDto bom)
    {
        try
        {
            var folder = GetFolder(folderPath);
            if (folder == null) return;

            if (bom == null)
            {
                SetModelVar(folder, "PartNo", "—");
                SetModelVar(folder, "Component", "—");
                SetModelVar(folder, "Operation Code", "—");
                SetModelVar(folder, "OperationNo", "—");
                SetModelVar(folder, "Operation Type", "—");
                SetModelVar(folder, "Units", "—");
                SetModelVar(folder, "Quantity", "—");
                SetModelVar(folder, "Active", "—");
                SetModelVar(folder, "Transfer Heat", "—");
                return;
            }

            SetModelVar(folder, "PartNo", bom.partNumber);
            SetModelVar(folder, "Component", bom.componentPartNumber);
            SetModelVar(folder, "Operation Code", bom.partOperationCode);
            SetModelVar(folder, "OperationNo", bom.partOperationNumber?.ToString());
            SetModelVar(folder, "Operation Type", bom.partOperationType);
            SetModelVar(folder, "Units", bom.componentUnitOfMeasure);
            SetModelVar(folder, "Quantity", bom.quantity?.ToString("F5"));
            SetModelVar(folder, "Active", bom.active?.ToString());
            SetModelVar(folder, "Transfer Heat", bom.transferHeat?.ToString());
        }
        catch (Exception ex)
        {
            Log.Error("BOMsLogic", $"PopulateModelVariables({folderPath}) failed: {ex.Message}");
        }
    }

    private IUANode GetFolder(string folderPath)
    {
        var folder = Project.Current.Get(folderPath);
        if (folder == null)
            Log.Warning("BOMsLogic", $"Could not find '{folderPath}' folder.");
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
                Log.Warning("BOMsLogic", $"Model variable '{variableName}' not found in folder.");
                return;
            }
            variable.Value = value ?? string.Empty;
        }
        catch (Exception ex)
        {
            Log.Error("BOMsLogic", $"SetModelVar({variableName}) failed: {ex.Message}");
        }
    }

    private string GetApiKey()
    {
        var apiKeyVar = LogicObject.GetVariable("apiKey");
        if (apiKeyVar == null)
        {
            Log.Error("BOMsLogic", "Variable 'apiKey' not found on LogicObject.");
            return null;
        }

        string apiKey = apiKeyVar.Value?.Value?.ToString();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Log.Warning("BOMsLogic", "apiKey is empty.");
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

                var url = $"{PlexURL}/engineering/v1/boms?partId={PartId}";
                var response = client.GetAsync(url).Result;
                response.EnsureSuccessStatusCode();
                return response.Content.ReadAsStringAsync().Result;
            }
        }
        catch (Exception ex)
        {
            Log.Error("BOMsLogic", $"HTTP request failed: {ex.Message}");
            return null;
        }
    }

    private class PlexBomDto
    {
        public string id { get; set; }
        public string partId { get; set; }
        public string partNumber { get; set; }
        public string partRevision { get; set; }
        public string partNumberRevision { get; set; }
        public string partOperationId { get; set; }
        public string partOperationCode { get; set; }
        public int? partOperationNumber { get; set; }
        public string partOperationType { get; set; }
        public string componentPartId { get; set; }
        public string componentPartNumber { get; set; }
        public string componentPartRevision { get; set; }
        public string componentPartNumberRevision { get; set; }
        public string componentSupplyItemId { get; set; }
        public string componentSupplyItemNumber { get; set; }
        public string componentUnitOfMeasure { get; set; }
        public decimal? quantity { get; set; }
        public decimal? minimumQuantity { get; set; }
        public decimal? maximumQuantity { get; set; }
        public bool? quantityFixed { get; set; }
        public string depletionUnitOfMeasure { get; set; }
        public decimal? depletionConversionFactor { get; set; }
        public decimal? sortOrder { get; set; }
        public bool? active { get; set; }
        public string position { get; set; }
        public string side { get; set; }
        public bool? scaling { get; set; }
        public bool? validate { get; set; }
        public bool? autoDeplete { get; set; }
        public bool? transferHeat { get; set; }
        public string note { get; set; }
    }
}
