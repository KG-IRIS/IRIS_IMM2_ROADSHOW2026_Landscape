#region Using directives
using FTOptix.NetLogic;
using FTOptix.UI;
using UAManagedCore;
using FTOptix.OPCUAServer;
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
using FTOptix.OPCUAClient;
#endregion

public class UserEditorPanelLoaderLogic : BaseNetLogic
{
    /// <summary>
    /// This method navigates to the User Details Panel based on the provided NodeId.
    /// If the user is null, the method returns immediately.
    /// It retrieves variables to determine which panel to display and updates the UsersList
    /// to select the specified user.
    /// </summary>
    /// <param name="user">The NodeId representing the user to display in the details panel.</param>
    [ExportMethod]
    public void GoToUserDetailsPanel(NodeId user)
    {
        if (user == null)
            return;

        var userCountVariable = LogicObject.GetVariable("UserCount");
        if (userCountVariable == null)
            return;

        var noUsersPanelVariable = LogicObject.GetVariable("NoUsersPanel");
        if (noUsersPanelVariable == null)
            return;

        var userDetailPanelVariable = LogicObject.GetVariable("UserDetailPanel");
        if (userDetailPanelVariable == null)
            return;

        var panelLoader = (PanelLoader)Owner;

        NodeId newPanelNode = userCountVariable.Value > 0 ? userDetailPanelVariable.Value : noUsersPanelVariable.Value;
        Owner.Owner.Get<ListBox>("UsersList").SelectedItem = user;

        panelLoader.ChangePanel(newPanelNode, user);
    }
}
