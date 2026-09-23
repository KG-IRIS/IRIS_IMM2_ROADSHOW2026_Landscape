#region Using directives
using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.WebUI;
using FTOptix.NetLogic;
using FTOptix.NativeUI;
using FTOptix.CoreBase;
using FTOptix.Alarm;
using FTOptix.RecipeX;
using FTOptix.DataLogger;
using FTOptix.EventLogger;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.Report;
using FTOptix.MQTTClient;
using FTOptix.RAEtherNetIP;
using FTOptix.System;
using FTOptix.Retentivity;
using FTOptix.CommunicationDriver;
using FTOptix.SerialPort;
using FTOptix.UI;
using FTOptix.Core;
using Microsoft.Playwright;
using FTOptix.OPCUAServer;
using FTOptix.OPCUAClient;
#endregion

public class FiixLoginManager : BaseNetLogic
{
    // private IUAVariable testMessage;

    private IUAVariable userVariable;
    private IUAVariable passwordVariable;
    private IUAVariable statusVariable;

    public override void Start()
    {
        Log.Info("FiixLoginManager started");

        // TestPlaywright();

        userVariable = Project.Current.GetVariable("Model/IrisLensSub/frcurruser");
        passwordVariable = Project.Current.GetVariable("Model/IrisLensSub/frcurrpass");
        statusVariable = Project.Current.GetVariable("Model/IrisLensSub/frstatus");

        userVariable.VariableChange += UserChanged;
        
    }

    // private async void TestPlaywright()
    // {
    //     try
    //     {
    //         Log.Info("Starting Playwright test");

    //         using var playwright = await Playwright.CreateAsync();

    //         Log.Info("Playwright initialized successfully");
    //     }
    //     catch(Exception ex)
    //     {
    //         Log.Error("Playwright error: " + ex.Message);
    //     }
    // }

    private void UserChanged(object sender, VariableChangeEventArgs e)
    {
        string user = userVariable.Value;
        string password = passwordVariable.Value;
        string status = statusVariable.Value;

        Log.Info("FiixLoginManager", $"User={user}, Status={status}");
    }

    public override void Stop()
    {
        Log.Info("FiixLoginManager stopped");
    }
}

