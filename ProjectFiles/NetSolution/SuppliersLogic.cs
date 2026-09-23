#region Using directives
using System;
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

public class SuppliersLogic : BaseNetLogic
{
    private const string PlexURL = "https://test.connect.plex.com";
    private const string SupplierId = "eff10f69-d014-4c1c-9eeb-fc68a5986845";

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
                Log.Warning("SuppliersLogic", "Empty response from Plex.");
                return;
            }

            var dto = JsonConvert.DeserializeObject<PlexSupplierDto>(json);
            if (dto == null)
            {
                Log.Warning("SuppliersLogic", "Deserialization returned null.");
                return;
            }

            PopulateModelVariables(dto);
        }
        catch (Exception ex)
        {
            Log.Error("SuppliersLogic", $"FetchAndDisplay failed: {ex.Message}");
        }
    }

    private void PopulateModelVariables(PlexSupplierDto dto)
    {
        try
        {
            var folder = GetFolder();
            if (folder == null) return;

            if (dto == null)
            {
                SetModelVar(folder, "Name", "—");
                SetModelVar(folder, "Status", "—");
                SetModelVar(folder, "Type", "—");
                SetModelVar(folder, "Web Address", "—");
                SetModelVar(folder, "Notes", "—");
                return;
            }

            SetModelVar(folder, "Name", dto.name);
            SetModelVar(folder, "Status", dto.status);
            SetModelVar(folder, "Type", dto.type);
            SetModelVar(folder, "Web Address", dto.webAddress);
            SetModelVar(folder, "Notes", dto.note);
        }
        catch (Exception ex)
        {
            Log.Error("SuppliersLogic", $"PopulateModelVariables failed: {ex.Message}");
        }
    }

    private IUANode GetFolder()
    {
        var folder = Project.Current.Get("Model/Plex/Suppliers");
        if (folder == null)
            Log.Warning("SuppliersLogic", "Could not find 'Model/Plex/Suppliers' folder.");
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
                Log.Warning("SuppliersLogic", $"Model variable '{variableName}' not found in 'Model/Plex/Suppliers'.");
                return;
            }
            variable.Value = value ?? string.Empty;
        }
        catch (Exception ex)
        {
            Log.Error("SuppliersLogic", $"SetModelVar({variableName}) failed: {ex.Message}");
        }
    }

    private string GetApiKey()
    {
        var apiKeyVar = LogicObject.GetVariable("apiKey");
        if (apiKeyVar == null)
        {
            Log.Error("SuppliersLogic", "Variable 'apiKey' not found on LogicObject.");
            return null;
        }

        string apiKey = apiKeyVar.Value?.Value?.ToString();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Log.Warning("SuppliersLogic", "apiKey is empty.");
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

                var url = $"{PlexURL}/mdm/v1/suppliers/{SupplierId}";
                var response = client.GetAsync(url).Result;
                response.EnsureSuccessStatusCode();
                return response.Content.ReadAsStringAsync().Result;
            }
        }
        catch (Exception ex)
        {
            Log.Error("SuppliersLogic", $"HTTP request failed: {ex.Message}");
            return null;
        }
    }

    private class PlexSupplierDto
    {
        public string id { get; set; }
        public string code { get; set; }
        public string name { get; set; }
        public string status { get; set; }
        public string type { get; set; }
        public string webAddress { get; set; }
        public string note { get; set; }
    }
}
