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

public class PlexRecordParts : BaseNetLogic
{
    private const string PlexURL = "https://kendall-disc.test.on.plex.com";
    private const string DataSourceId = "20446";
    private const string BasicAuthHeader = "Basic SXJpc0lBRGlzY3JldGVXc0BwbGV4LmNvbTphMDc5NjkyLWIzMTctNA==";

    private const decimal CycleTimeTrigger = 30m;
    private const decimal CycleTimeReset = 5m;

    private IUAVariable _cycleTimeVar;
    private bool _recorded;

    public override void Start()
    {
        try
        {
            //_cycleTimeVar = Project.Current.GetVariable("Model/InjectionMoldingMachine/CycleTime");
            //Model / HTML_IMM / IMM1fromBroker / Status / shotCount
            _cycleTimeVar = Project.Current.GetVariable("Model/HTML_IMM/IMM1fromBroker/Status/shotCount");
            //Log.Error("PlexAPIs", "Variable 'Model/HTML_IMM/IMM1fromBroker/Status/shotCount' = " );
            if (_cycleTimeVar == null)
            {
                Log.Error("PlexAPIs", "Variable 'Model/InjectionMoldingMachine/CycleTime' not found.");
                return;
            }

            _cycleTimeVar.VariableChange += CycleTime_VariableChange;
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
            if (_cycleTimeVar != null)
                _cycleTimeVar.VariableChange -= CycleTime_VariableChange;
        }
        catch (Exception ex)
        {
            Log.Error("PlexAPIs", $"Stop failed: {ex.Message}");
        }
    }

    private void CycleTime_VariableChange(object sender, VariableChangeEventArgs e)
    {
        try
        {
            //decimal cycleTime = ToDecimal(e.NewValue?.Value);

            //if (cycleTime >= CycleTimeTrigger)
            //{
           //Log.Error("PlexAPIs", $"Variable '...shotCount' = {e.NewValue?.Value}");


            if (!_recorded)
                {
                    int workcenterKey = GetWorkcenterKey();
                    if (workcenterKey < 0) return;

                    string workcenterLabel = GetWorkcenterLabel();

                    RecordProduction(workcenterKey, workcenterLabel);
                    _recorded = true;
                }
            //}
            else //if (cycleTime <= CycleTimeReset)
            {
                _recorded = false;
            }
        }
        catch (Exception ex)
        {
            Log.Error("PlexAPIs", $"CycleTime_VariableChange failed: {ex.Message}");
        }
    }

    private IUANode GetWorkcenterNode()
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

    private int GetWorkcenterKey()
    {
        var node = GetWorkcenterNode();
        if (node == null) return -1;

        var keyVar = node.GetVariable("Workcenter_Key");
        if (keyVar == null)
        {
            Log.Error("PlexAPIs", $"Workcenter_Key not found on '{node.BrowseName}'.");
            return -1;
        }

        if (!int.TryParse(keyVar.Value?.Value?.ToString(), out int key))
        {
            Log.Error("PlexAPIs", $"Workcenter_Key could not be parsed as integer on '{node.BrowseName}'.");
            return -1;
        }

        return key;
    }

    private string GetWorkcenterLabel()
    {
        var node = GetWorkcenterNode();
        return node?.BrowseName ?? "Unknown";
    }

    // --- Production recording ---

    private void RecordProduction(int workcenterKey, string label)
    {
        try
        {
            var body = new ProductionRecordRequest
            {
                inputs = new ProductionRecordInputs
                {
                    Workcenter_Key = workcenterKey,
                    PLC_Name = "s-2",
                    Quantity = 1,
                    Container_Full = true,
                    Container_Status = "OK",
                    Container_Note = "",
                    Scrap_Quantity = 0,
                    Scrap_Reason = "",
                    Add_To_Master = 0,
                    Master_Unit_No = "NEW",
                    Validate_Only = false
                }
            };

            string response = Post($"/api/datasources/{DataSourceId}/execute", JsonConvert.SerializeObject(body));

            if (response == null)
                Log.Warning("PlexAPIs", $"{label}: production record failed.");
        }
        catch (Exception ex)
        {
            Log.Error("PlexAPIs", $"RecordProduction({label}) failed: {ex.Message}");
        }
    }

    private decimal ToDecimal(object value)
    {
        if (value == null) return 0m;
        try
        {
            return Convert.ToDecimal(value);
        }
        catch
        {
            return decimal.TryParse(value.ToString(), out decimal parsed) ? parsed : 0m;
        }
    }

    private string Post(string endpoint, string jsonBody)
    {
        try
        {
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.Add("Accept", "application/json");
                client.DefaultRequestHeaders.Add("Authorization", BasicAuthHeader);

                var content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
                content.Headers.ContentType.CharSet = "";

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

    private class ProductionRecordRequest
    {
        public ProductionRecordInputs inputs { get; set; }
    }

    private class ProductionRecordInputs
    {
        public int Workcenter_Key { get; set; }
        public string PLC_Name { get; set; }
        public int Quantity { get; set; }
        public bool Container_Full { get; set; }
        public string Container_Status { get; set; }
        public string Container_Note { get; set; }
        public int Scrap_Quantity { get; set; }
        public string Scrap_Reason { get; set; }
        public int Add_To_Master { get; set; }
        public string Master_Unit_No { get; set; }
        public bool Validate_Only { get; set; }
    }
}
