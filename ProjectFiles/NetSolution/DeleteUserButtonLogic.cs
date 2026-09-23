#region Using directives
using System.Linq;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.UI;
using UAManagedCore;
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

public class DeleteUserButtonLogic : BaseNetLogic
{
    /// <summary>
    /// This method deletes a user from the user list by their NodeId.
    /// It first retrieves the user object, checks for its existence, and then removes it from the users folder.
    /// If the user exists, it updates the user list to reflect the deletion.
    /// </summary>
    /// <param name="userToDelete">The NodeId of the user to be deleted.</param>
    /// <note>
    /// The method assumes that the <see cref="InformationModel"/> and <see cref="Owner"/> are properly initialized.
    /// </note>
    [ExportMethod]
    public void DeleteUser(NodeId userToDelete)
    {
        var userObjectToRemove = InformationModel.Get(userToDelete);
        if (userObjectToRemove == null)
        {
            Log.Error("UserEditor", "Cannot obtain the selected user.");
            return;
        }

        var userVariable = Owner.Owner.Owner.Owner.GetVariable("Users");
        if (userVariable == null)
        {
            Log.Error("UserEditor", "Missing user variable in UserEditor Panel.");
            return;
        }

        if (userVariable.Value == null || (NodeId) userVariable.Value == NodeId.Empty)
        {
            Log.Error("UserEditor", "Fill User variable in UserEditor.");
            return;
        }
        var usersFolder = InformationModel.Get(userVariable.Value);
        if (usersFolder == null)
        {
            Log.Error("UserEditor", "Cannot obtain Users folder.");
            return;
        }

        usersFolder.Remove(userObjectToRemove);

        if (usersFolder.Children.Count > 0)
        {
            var usersList = Owner.Owner.Owner.Get<ListBox>("HorizontalLayout1/UsersList");
            usersList.SelectedItem = usersFolder.Children.First().NodeId;
        }
    }
}
