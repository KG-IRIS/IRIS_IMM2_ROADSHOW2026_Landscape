#region Using directives
using System;
using FTOptix.HMIProject;
using UAManagedCore;
using FTOptix.NetLogic;
using FTOptix.Alarm;
using FTOptix.WebUI;
using FTOptix.RecipeX;
using FTOptix.Store;
using FTOptix.Report;
using FTOptix.RAEtherNetIP;
using FTOptix.CommunicationDriver;
using FTOptix.SQLiteStore;
using FTOptix.ODBCStore;
using FTOptix.InfluxDBStore;
using FTOptix.InfluxDBStoreRemote;
using FTOptix.InfluxDBStoreLocal;
using FTOptix.EventLogger;
using FTOptix.DataLogger;
using FTOptix.MQTTClient;
using FTOptix.OPCUAServer;
using FTOptix.OPCUAClient;
#endregion

public class ClockLogic : BaseNetLogic
{
	public override void Start()
	{
		periodicTask = new PeriodicTask(UpdateTime, 1000, LogicObject);
		periodicTask.Start();
	}

	public override void Stop()
	{
		periodicTask.Dispose();
		periodicTask = null;
	}

	/// <summary>
	/// This method updates the current time for both local and UTC timestamps.
	/// <example>
	/// For example:
	/// <code>
	/// UpdateTime();
	/// </code>
	/// will update the variables with the current local and UTC times.
	/// </example>
	/// </summary>
	/// <remarks>
	/// The method retrieves the current local and UTC times using `Now` and `UtcNow`, respectively.
	/// It then sets the values of corresponding logic object variables (e.g., Time, Year, Month, Day, Hour, Minute, Second).
	/// Additionally, it stores the UTC timestamp values in their respective variables.
	/// </remarks>
	private void UpdateTime()
	{
		DateTimeOffset now = DateTimeOffset.Now;
		DateTime localTime = now.LocalDateTime;
		DateTime utcTime = now.UtcDateTime;

		LogicObject.GetVariable("Time").Value = localTime;
		LogicObject.GetVariable("Time/Year").Value = localTime.Year;
		LogicObject.GetVariable("Time/Month").Value = localTime.Month;
		LogicObject.GetVariable("Time/Day").Value = localTime.Day;
		LogicObject.GetVariable("Time/Hour").Value = localTime.Hour;
		LogicObject.GetVariable("Time/Minute").Value = localTime.Minute;
		LogicObject.GetVariable("Time/Second").Value = localTime.Second;
		LogicObject.GetVariable("UTCTime").Value = utcTime;
		LogicObject.GetVariable("UTCTime/Year").Value = utcTime.Year;
		LogicObject.GetVariable("UTCTime/Month").Value = utcTime.Month;
		LogicObject.GetVariable("UTCTime/Day").Value = utcTime.Day;
		LogicObject.GetVariable("UTCTime/Hour").Value = utcTime.Hour;
		LogicObject.GetVariable("UTCTime/Minute").Value = utcTime.Minute;
		LogicObject.GetVariable("UTCTime/Second").Value = utcTime.Second;
	}

	private PeriodicTask periodicTask;
}
