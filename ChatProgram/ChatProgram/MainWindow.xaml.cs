using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Net;
using NetworkCommsDotNet;
using NetworkCommsDotNet.DPSBase;
using NetworkCommsDotNet.Tools;
using NetworkCommsDotNet.Connections;
using NetworkCommsDotNet.Connections.TCP;

namespace ChatProgram
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        #region Private Fields
        /// <summary>
        /// Dictionary to keep track of which peer messages have already been written to the chat window
        /// </summary>
        Dictionary<ShortGuid, ChatMessage>
            lastPeerMessageDict = new Dictionary<ShortGuid, ChatMessage>();
        /// <summary>
        /// The maximum number of times a chat message will be relayed
        /// </summary> int relayMaximum = 3;

        /// <summary>
        /// An optional encryption key to use should one be required.
        /// This can be changed freely but must obviously be the same
        /// for both sender and receiver.
        /// </summary>
        string encryptionKey = "ljlhjf8uyfln23490jf;m21-=scm20--iflmk;";
        /// <summary>
        /// A local counter used to track the number of messages sent from
        /// this instance.
        /// </summary>
        long messageSendIndex = 0;
        #endregion
        /// <summary>
        /// Append the provided message to the chatBox text box.
        /// </summary>
        /// <param name="message"></param>
        private void AppendLineToChatBox(string message)
        {
            //To ensure we can successfully append to the text box from any thread
            //we need to wrap the append within an invoke action.
            chatBox.Dispatcher.BeginInvoke(new Action<string>((messageToAdd) =>
            {
                chatBox.AppendText(messageToAdd + "\n");
                chatBox.ScrollToEnd();
            }), new object[] { message });
        }
        /// <summary>
        /// Refresh the messagesFrom text box using the recent message history.
        /// </summary>
        private void RefreshMessagesFromBox()
        {
            //We will perform a lock here to ensure the text box is only
            //updated one thread at a time
            lock(lastPeerMessageDict)
            {
                //Use a linq expression to extract an array of all current users from lastPeerMessageDict
                string[] currentUsers = (from current in lastPeerMessageDict.Values orderby current.SourceName select current.SourceName).ToArray();
                //To ensure we can successfully append to the text box from any thread
                //we need to wrap the append within an invoke action.
                this.messagesFrom.Dispatcher.BeginInvoke(new Action<string[]>((users) =>
                {
                    //First clear the text box
                    messagesFrom.Text = "";
                    //Now write out each username
                    foreach (var username in users)
                        messagesFrom.AppendText(username + "\n");
                }), new object[] { currentUsers });
            }
        }

        /// <summary>
        /// Send any entered message when we click the send button.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void SendMessageButton_Click(object sender, RoutedEventArgs e)
        {
            SendMessage();
        }
        /// <summary>
        /// Send any entered message when we press enter or return
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MessageText_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Return)
                SendMessage();
        }
        /// <summary>
        /// Toggle encryption
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void UseEncryptionBox_CheckedToggle(object sender, RoutedEventArgs e)
        {
            if (useEncryptionBox.IsChecked != null && (bool)useEncryptionBox.IsChecked)
            {
                RijndaelPSKEncrypter.AddPasswordToOptions(NetworkComms.DefaultSendReceiveOptions.Options, encryptionKey);
                NetworkComms.DefaultSendReceiveOptions.DataProcessors.Add(DPSManager.GetDataProcessor<RijndaelPSKEncrypter>());
            }
            else
            NetworkComms.DefaultSendReceiveOptions.DataProcessors.Remove(DPSManager.GetDataProcessor<RijndaelPSKEncrypter>());
        }
        /// <summary>
        /// Correctly shutdown NetworkComms .Net when closing the WPF application
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            //Ensure we shutdown comms when we are finished
            NetworkComms.Shutdown();
        }
        /// <summary>
        /// Toggle whether the local application is acting as a server
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void EnableServer_Toggle(object sender, RoutedEventArgs e)
        {
            //Enable or disable the local server mode depending on the checkbox IsChecked value
            if (enableServer.IsChecked != null && (bool)enableServer.IsChecked)
                ToggleServerMode(true);
            else
                ToggleServerMode(false);
        }

        /// <summary>
        /// Wrap the functionality required to enable/disable the local application server mode
        /// </summary>
        /// <param name="enableServer"></param>
        private void ToggleServerMode(bool enableServer)
        {
            if (enableServer)
            {
                //Start listening for new incoming TCP connections
                //Parameters ensure we listen across all adaptors using a random port
                Connection.StartListening(ConnectionType.TCP, new IPEndPoint(IPAddress.Any, 0));
                //Write the IP addresses and ports that we are listening on to the chatBox
                chatBox.AppendText("Listening for incoming TCP connections on:\n");
                foreach (IPEndPoint listenEndPoint in Connection.ExistingLocalListenEndPoints(ConnectionType.TCP))
                    chatBox.AppendText(listenEndPoint.Address + ":" + listenEndPoint.Port + "\n");
            }
            else
            {
                NetworkComms.Shutdown();
                chatBox.AppendText("Server disabled. No longer accepting connections and all existing connections have been closed.");
            }
        }

        /// <summary>
        /// Performs whatever functions we might so desire when we receive an incoming ChatMessage
        /// </summary>
        /// <param name="header">The PacketHeader corresponding with the received object</param>
        /// <param name="connection">The Connection from which this object was received</param>
        /// <param name="incomingMessage">The incoming ChatMessage we are after</param>
        private void HandleIncomingChatMessage(PacketHeader header, Connection connection, ChatMessage incomingMessage)
        {
            //We only want to write a message once to the chat window
            //Because we allow relaying and may receive the same message twice
            //we use our history and message indexes to ensure we have a new message
            lock (lastPeerMessageDict)
            {
                if (lastPeerMessageDict.ContainsKey(incomingMessage.SourceIdentifier))
                {
                    if (lastPeerMessageDict[incomingMessage.SourceIdentifier].MessageIndex < incomingMessage.MessageIndex)
                    {
                        //If this message index is greater than the last seen from this source we can safely
                        //write the message to the ChatBox
                        AppendLineToChatBox(incomingMessage.SourceName + " - " + incomingMessage.Message);
                        //We now replace the last received message with the current one
                        lastPeerMessageDict[incomingMessage.SourceIdentifier] = incomingMessage;
                    }
                }
                else
                {
                    //If we have never had a message from this source before then it has to be new
                    //by definition
                    lastPeerMessageDict.Add(incomingMessage.SourceIdentifier, incomingMessage);
                    AppendLineToChatBox(incomingMessage.SourceName + " - " + incomingMessage.Message);
                }
            }
            //Once we have written to the ChatBox we refresh the MessagesFromWindow
            RefreshMessagesFromBox();
            //This last section of the method is the relay function
            //We start by checking to see if this message has already been relayed
            //the maximum number of times
            if (incomingMessage.RelayCount < relayMaximum)
            {
                //If we are going to relay this message we need an array of
                //all other known connections
                var allRelayConnections = (from current in NetworkComms.GetExistingConnection() where current != connection select current).ToArray();
                //We increment the relay count before we send
                incomingMessage.IncrementRelayCount();
                //We will now send the message to every other connection
                foreach (var relayConnection in allRelayConnections)
                {
                    //We ensure we perform the send within a try catch
                    //To ensure a single failed send will not prevent the
                    //relay to all working connections.
                    try
                    {
                        relayConnection.SendObject("ChatMessage", incomingMessage);
                    }
                    catch (CommsException)
                    {
                        // Catch the comms exception, ignore and continue
                    }
                }
            }
        }

        /// <summary>
        /// Performs whatever functions we might so desire when an existing connection is closed.
        /// </summary>
        /// <param name="connection">The closed connection</param>
        private void HandleConnectionClosed(Connection connection)
        {
            //We are going to write a message to the ChatBox when a user disconnects
            //We perform the following within a lock so that threads proceed one at a time
            lock (lastPeerMessageDict)
            {
                //Extract the remoteIdentifier from the closed connection
                ShortGuid remoteIdentifier = connection.ConnectionInfo.NetworkIdentifier;
                //If at some point we received a message with this identifier we can
                //include the source name in the disconnection message.
                if (lastPeerMessageDict.ContainsKey(remoteIdentifier))
                    AppendLineToChatBox("Connection with '" + lastPeerMessageDict[remoteIdentifier].SourceName + "' has been closed.");
                else
                    AppendLineToChatBox("Connection with '" + connection.ToString() + "' has been closed.");
                //Last thing is to remove this entry from our message history
                lastPeerMessageDict.Remove(connection.ConnectionInfo.NetworkIdentifier);
            }
            //Refresh the messages from box to reflect this disconnection
            RefreshMessagesFromBox();
        }

        /// <summary>
        /// Send our message.
        /// </summary>
        private void SendMessage()
        {
            //If we have tried to send a zero length string we just return
            if (messageText.Text.Trim() == "")
                return;
            //We may or may not have entered some server connection information
            ConnectionInfo serverConnectionInfo = null;
            if (serverIP.Text != "")
            {
                try
                {
                    serverConnectionInfo = new ConnectionInfo(serverIP.Text.Trim(), int.Parse(serverPort.Text));
                }
                catch (Exception)
                {
                    MessageBox.Show("Failed to parse the server IP and port. Please ensure it is correct and try again", "Server IP & Port Parse Error", MessageBoxButton.OK);
                    return;
                }
            }
            //We wrap everything we want to send in the ChatMessage class we created
            ChatMessage messageToSend = new ChatMessage(NetworkComms.NetworkIdentifier, localName.Text, messageText.Text, messageSendIndex++);
            //We add our own message to the message history in-case it gets relayed back to us
            lock (lastPeerMessageDict) lastPeerMessageDict[NetworkComms.NetworkIdentifier] = messageToSend;
            //We write our own message to the chatBox
            AppendLineToChatBox(messageToSend.SourceName + " - " + messageToSend.Message);
            //We refresh the MessagesFrom box so that it includes our own name
            RefreshMessagesFromBox();
            //We clear the text within the messageText box.
            this.messageText.Text = "";
            //If we provided server information we send to the server first
            if (serverConnectionInfo != null)
            {
                //We perform the send within a try catch to ensure the application continues to run if there is a problem.
                try
                {
                    TCPConnection.GetConnection(serverConnectionInfo).SendObject("ChatMessage", messageToSend);
                }
                catch (CommsException)
                {
                    MessageBox.Show("A CommsException occurred while trying to send message to " + serverConnectionInfo, "CommsException", MessageBoxButton.OK);
                }
            }
            //If we have any other connections we now send the message to those as well
            //This ensures that if we are the server everyone who is connected to us gets our message
            var otherConnectionInfos = (from current in NetworkComms.AllConnectionInfo() where current != serverConnectionInfo select current).ToArray();
            foreach (ConnectionInfo info in otherConnectionInfos)
            {
                //We perform the send within a try catch to ensure the application continues to run if there is a problem.
                try
                {
                    TCPConnection.GetConnection(info).SendObject("ChatMessage", messageToSend);
                }
                catch (CommsException)
                {
                    MessageBox.Show("A CommsException occurred while trying to send message to " + info, "CommsException", MessageBoxButton.OK);
                }
            }
        }
        public MainWindow()
        {
            InitializeComponent();
            //Write the IP addresses and ports that we are listening on to the chatBox
            chatBox.AppendText("Initialised WPF chat example.");
            //Add a blank line after the initialisation output
            chatBox.AppendText("\n");
            //Set the default Local Name box using to the local host name
            localName.Text = HostInfo.HostName;
            //Configure NetworkComms .Net to handle and incoming packet of type 'ChatMessage'
            //e.g. If we receive a packet of type 'ChatMessage' execute the method 'HandleIncomingChatMessage'
            NetworkComms.AppendGlobalIncomingPacketHandler<ChatMessage>("ChatMessage", HandleIncomingChatMessage);
            //Configure NetworkComms .Net to perform an action when a connection is closed
            //e.g. When a connection is closed execute the method 'HandleConnectionClosed'
            NetworkComms.AppendGlobalConnectionCloseHandler(HandleConnectionClosed);
        }
    }
}
