/*
 * Created by SharpDevelop.
 * User: David
 * Date: 2/22/2019
 * Time: 6:24 PM
 * 
 * To change this template use Tools | Options | Coding | Edit Standard Headers.
 */

using System;
using System.Text;
using System.Xml;
using System.Collections.Generic;
using System.Globalization;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BlockHelper
{
	/// <summary>
	/// Description of NTP1Script.
	/// </summary>
	public partial class Program
	{
		//RPC user values
		public static string rpc_username = "";
		public static string rpc_pass = "";
		public static int rpc_port = 6326;
		public static string rpc_endpoint = "127.0.0.1";
		public static bool server_active = false;
		public static TcpListener blockhelper_server = null;
		
		//This server supports more than one connection
		public static List<ServerConnection> ConnectionList = new List<ServerConnection>();
		public static AsyncCallback ConnCallback = new AsyncCallback(ConnectionCallback);
		
		public class ServerConnection
		{
			public string[] ip_address = new string[2]; //Ip address and port
			public bool open;
			public TcpClient client;
			public NetworkStream stream;
			public Byte[] buf = new Byte[256];
			public int lasttouchtime; //Last time the connect was touched
			public ServerConnection()
			{
				open = true;
				lasttouchtime = UTCTime();
			}
		}
		
		//This code activates the server asynchronously
		public static async void SetServer(bool activate)
		{
			if(activate == false && blockhelper_server != null){
				//We need to disconnect the critical node
				server_active = false;
				blockhelper_server.Stop(); //Will cause an exception from running server
				BlockHelperLog("Server Stopped");
				blockhelper_server = null;
				
				//Drop any connections as well
				lock(ConnectionList){
					for(int i = 0;i < ConnectionList.Count;i++){
						ConnectionList[i].open = false; //Close the connection
					}
				}
				
				return;
			}
			
			//When this function is ran, it starts the server
			if(activate == true && blockhelper_server == null){
				try {
					server_active = true;
					blockhelper_server = new TcpListener(IPAddress.Any,blockhelper_port);
					blockhelper_server.Start(); //Start to listen at this port
					BlockHelperLog("Server Listening...");
					while(server_active == true){
						//This status may change
						try {
							TcpClient client = await blockhelper_server.AcceptTcpClientAsync(); //This will wait for a client
							//The IP address of the person on the other side and port they used to connect to
							//A new client connected, create a connection
							string ip_add = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
							string port = ((IPEndPoint)client.Client.RemoteEndPoint).Port.ToString();
							
			            	BlockHelperLog("Server: Connection Accepted: "+ip_add);
			            	ServerConnection con = new ServerConnection();
			            	con.ip_address[0] = ip_add;
			            	//Accept only local connections and drop others
			            	if(ip_add != "127.0.0.1"){
			            		BlockHelperLog("Not local connection, dropping it");
			            		client.Close();
			            		continue;
			            	}
			            	con.ip_address[1] = port;
			            	con.client = client;
			            	con.stream = client.GetStream();     	
			            	//Setup the callback that runs when data is received into connection
			            	con.stream.BeginRead(con.buf,0,1,ConnCallback,con);
			            	lock(ConnectionList){
			            		ConnectionList.Add(con);
			            	}
						} catch (Exception e) {
							BlockHelperLog("Failed to connect to a client, error: "+e.ToString());
						}
					}
					BlockHelperLog("Server Stopped Listening");
				} catch (Exception e) {
					BlockHelperLog("Disconnected server, error: "+e.ToString());
					server_active = false;
					blockhelper_server = null;
				}
			}
		}
		
		public static void ConnectionCallback(IAsyncResult asyncResult)
		{
			//This function is called when there is data returned from async callback
			ServerConnection con = (ServerConnection)asyncResult.AsyncState; //The object passed into the callback
			try {
				if(con.open == false){return;} //Stream is already closed
				int bytesread = con.stream.EndRead(asyncResult); //Get the bytes to read
				if(bytesread == 0){
					//Nothing to read as connection has been disconnected
					con.open = false;			
					return;
				}else{
					//Something to read, so keep reading it
					//Get the total bytes of the packet
					Byte[] data_size_buf = new Byte[4];
					data_size_buf[0] = con.buf[0]; //The first byte was received so use that
					con.stream.Read(data_size_buf,1,3); //Get the remaining bytes
					uint data_size = Bytes2Uint(data_size_buf); //Raw data is little endian
					if(data_size > 50000){
						//Messages should be less than 50kb
						//Transaction broadcast should be the largest messages
						con.open = false;return;
					}
					string msg="";
					byte[] read_buf = new byte[256];
					while(data_size > 0){
						int read_amount = read_buf.Length;
						if(read_amount > data_size){read_amount = (int)data_size;}
						int read_size = con.stream.Read(read_buf,0,read_amount);
						//Get the string
						msg = msg + System.Text.Encoding.ASCII.GetString(read_buf, 0, read_size);
						if(data_size - read_size <= 0){break;}
						data_size -= Convert.ToUInt32(read_size);
					}
					Task.Run(() => ProcessConnectionMsg(con,msg) );
					con.lasttouchtime = UTCTime(); //There is activity on this connection
					con.stream.BeginRead(con.buf,0,1,ConnCallback,con); //Again wait for more data from the stream
				}				
			} catch (Exception e) {
				BlockHelperLog("Connection could not be read: "+con.ip_address[0]+", error: "+e.ToString());
				con.open = false;
			}
		}

		public static void ProcessConnectionMsg(ServerConnection con,string msg)
		{
			//This will take the message and the connection and process it
			try {
				JObject js = JObject.Parse(msg); //Parse this message
				JObject resp = null;
				BlockHelperLog("Received message from "+con.ip_address[0]+": "+msg);

				int response = Convert.ToInt32(js["rpc.response"].ToString()); //This shows if we are requesting a response
				string method = js["rpc.method"].ToString();
				
				switch (method) {
					case "rpc.serverversion":
						//Keep alive/verify that server is working correctly
						resp = new JObject();
						resp["rpc.method"] = "rpc.serverversion";
						resp["rpc.response"] = 1;
						resp["rpc.result"] = version_text; //Tell the CN the version of the RPC
						SendServerMessage(con,resp);
						break;
					case "rpc.gettransactioninfo":
						//The CN is requesting transaction information
						string target_tx = js["rpc.txhash"].ToString();
						JObject tx_json = GetTxJSONFromDatabase(target_tx);
						if(tx_json == null){
							resp = new JObject();
							resp["rpc.method"] = "rpc.gettransactioninfo";
							resp["rpc.response"] = 1;
							resp["rpc.error"] = "No transaction with that hash exists in database";
						}else{
							//The transaction does exist,
							resp = new JObject();
							resp["rpc.method"] = "rpc.gettransactioninfo";
							resp["rpc.response"] = 1;
							resp["rpc.result"] = tx_json; //The result is the JObject
						}
						SendServerMessage(con,resp);
						break;
					case "rpc.getaddressinfo":
						//The CN is requesting unspent utxos at a specific address
						string target_address = js["rpc.neb_address"].ToString();
						JArray add_utxos = GetUnspentUTXOJSONFromDatabase(target_address);
						if(add_utxos == null){
							resp = new JObject();
							resp["rpc.method"] = "rpc.getaddressinfo";
							resp["rpc.response"] = 1;
							resp["rpc.error"] = "Could not find unspent UTXOs for this Neblio address";
						}else{
							resp = new JObject();
							resp["rpc.method"] = "rpc.getaddressinfo";
							resp["rpc.response"] = 1;
							resp["rpc.result"] = add_utxos; //The result is the Jarray
						}
						SendServerMessage(con,resp);
						break;
					case "rpc.broadcasttx":
						//The CN is broadcasting a tx to the network
						string tx_hash = BroadcastRPCTransaction(js["rpc.tx_hex"].ToString());
						if(tx_hash.Length == 0){
							resp = new JObject();
							resp["rpc.method"] = "rpc.broadcasttx";
							resp["rpc.response"] = 1;
							resp["rpc.error"] = "Failed to broadcast Neblio transaction";
						}else{
							//The transaction does exist,
							resp = new JObject();
							resp["rpc.method"] = "rpc.broadcasttx";
							resp["rpc.response"] = 1;
							resp["rpc.result"] = tx_hash;
						}
						SendServerMessage(con,resp);
						break;
					case "rpc.getspentaddressinfo":
						//The CN is requesting recently spent utxo for a specific address
						target_address = js["rpc.neb_address"].ToString();
						int num_utxo = Convert.ToInt32(js["rpc.max_utxo"].ToString());
						if(num_utxo < 1){num_utxo = 1;} //Bounds
						if(num_utxo > 1000){num_utxo = 1000;} 
						add_utxos = GetSpentUTXOJSONFromDatabase(target_address,num_utxo);
						if(add_utxos == null){
							resp = new JObject();
							resp["rpc.method"] = "rpc.getspentaddressinfo";
							resp["rpc.response"] = 1;
							resp["rpc.error"] = "Could not find spent UTXOs for this Neblio address";
						}else{
							resp = new JObject();
							resp["rpc.method"] = "rpc.getspentaddressinfo";
							resp["rpc.response"] = 1;
							resp["rpc.result"] = add_utxos; //The result is the Jarray
						}
						SendServerMessage(con,resp);
						break;
					case "rpc.createntp1transaction":
						//The CN is requesting the blockhelper to create an unsigned ntp1 transaction that
						//spends the specified tokens. Order of tokens is very important.
						string from_add = js["rpc.source_address"].ToString();
						JArray to_adds = JArray.Parse(js["rpc.to_addresses"].ToString()); //Get the array of where the tokens are going
						JObject tx_info = CreateScratchNTP1Transaction(from_add,to_adds); //Generates script from scratch based on protocol rules
						if(tx_info == null){
							resp = new JObject();
							resp["rpc.method"] = "rpc.createntp1transaction";
							resp["rpc.response"] = 1;
							resp["rpc.error"] = "Could not create an NTP1 transaction of the desired amounts. Make sure to follow API intructions for to addresses.";
						}else{
							resp = new JObject();
							resp["rpc.method"] = "rpc.createntp1transaction";
							resp["rpc.response"] = 1;
							resp["rpc.result"] = tx_info; //The result is a Jobject that contains information to create a transaction
						}
						SendServerMessage(con,resp);
						break;
					case "rpc.getblockheight":
						//The CN is requesting the blockheight of the database
						long blockheight = GetDatabaseBlockHeight();
						if(blockheight < 0){
							resp = new JObject();
							resp["rpc.method"] = "rpc.getblockheight";
							resp["rpc.response"] = 1;
							resp["rpc.error"] = "No blockheight information available";
						}else{
							resp = new JObject();
							resp["rpc.method"] = "rpc.getblockheight";
							resp["rpc.response"] = 1;
							resp["rpc.result"] = blockheight; //The result is a long
						}
						SendServerMessage(con,resp);
						break;
				}
			} catch (Exception e) {
				BlockHelperLog("Error parsing message: "+msg+", error: "+e.ToString());
				con.open = false; //Close the connection for malformed messages
			}
		}
		
		public static bool SendServerMessage(ServerConnection con,JObject msg)
		{
			//This method serialize a jobject and send a message
			if(msg == null){return false;}
			string msg_encoded = JsonConvert.SerializeObject(msg);
			if(msg_encoded.Length == 0){return false;}
			//Now add data length and send data
			Byte[] data = System.Text.Encoding.ASCII.GetBytes(msg_encoded);
			uint data_length = (uint)msg_encoded.Length;
			lock(con)
			{
				if(con.open == false){return false;}
				try {
					con.stream.Write(Uint2Bytes(data_length),0,4); //Write the length of the bytes to be received
					con.stream.Write(data, 0, data.Length);
					return true;					
				} catch (Exception e) {
					BlockHelperLog("Failed to send message. Connection Disconnected: "+con.ip_address[0]+", error: "+e.ToString());
					con.open = false;
				}		
			}
			return false;
		}
		
		public static void CheckServerConnections()
		{
			//This function periodically checks the server connections and closes them if 2 minutes without query
			lock(ConnectionList){
				for(int i = 0;i < ConnectionList.Count;i++){
					ServerConnection sc = ConnectionList[i];
					if(UTCTime() - sc.lasttouchtime > 120){
						//Too long since this connection has received a message, close it
						sc.open = false;
					}
				}
			}
			
			//Go through the list again and remove any inactive connections
			lock(ConnectionList){
				for(int i = ConnectionList.Count-1;i >= 0;i--){
					ServerConnection sc = ConnectionList[i];
					if(sc.open == false){
						try {
							sc.stream.Close();
							sc.client.Close();	
						} catch (Exception) {}
						BlockHelperLog("Closed inbound connection at "+sc.ip_address[0]+", port: "+sc.ip_address[1]);
						ConnectionList.Remove(sc);
					}
				}
			}

			//Remove the debug log if it gets too large
			if(File.Exists(App_Path+"/blockdata/debug.log") == true){
				long filelength = new System.IO.FileInfo(App_Path+"/blockdata/debug.log").Length;
				if(filelength > 10000000){ //Debug log is greater than 10MB
					lock(debugfileLock){
						File.Delete(App_Path+"/blockdata/debug.log");
					}
				}
			}			
		}
		
		public static string RPCRequest(string postdata, out bool timeout,int timeout_sec = 30)
		{
			int request_times = 0;
			string responseString = "";
			timeout = false;
			while(request_times < 3){
				timeout = false;
				try {
					//This will make a request and return the result
					//30 second timeout
					//Post data must be formatted: var=something&var2=something
					
					//This prevents server changes from breaking the protocol
					//Webrequest will use the same TCP connection unless specified to close connection after request ends
					ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
					
					HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://"+rpc_endpoint+":"+rpc_port);
					request.Credentials = new NetworkCredential(rpc_username,rpc_pass);
					request.Timeout = 1000 * timeout_sec; //Wait thirty seconds by default
					
					if(postdata.Length > 0){ //It is a post request
						byte[] data = Encoding.ASCII.GetBytes(postdata);
						
						request.Method = "POST";
						request.ContentType = "application/json";
						request.ContentLength = data.Length;
						
						using (var stream = request.GetRequestStream())
						{
						    stream.Write(data, 0, data.Length); //Write post data
						}				
					}
					
					using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
					{
						using (StreamReader respreader = new StreamReader(response.GetResponseStream()))
						{
							responseString = respreader.ReadToEnd();
						}
					}
					break; //Leave the while loop, we got a good response
				} catch (WebException e){
					if(e.Status == WebExceptionStatus.Timeout){
						//The server is offline or computer is not connected to internet
						timeout = true;
						BlockHelperLog("The Server is offline or the computer is not connected to internet");
					}else{
						//Get the error and write the error to our log
						if(e.Response != null){
							StreamReader respreader = new StreamReader(e.Response.GetResponseStream());
							string err_string = respreader.ReadToEnd();
							respreader.Close();
							e.Response.Close();
							BlockHelperLog("Request error for: "+rpc_endpoint+", error:\n"+err_string);
						}else{
							timeout = true;
							BlockHelperLog("Connection error for: "+rpc_endpoint+", error:\n"+e.ToString()); //No stream available
						}
					}
				} catch (Exception e) {
					BlockHelperLog("Request error for: "+rpc_endpoint+", error:\n"+e.ToString());
				}
				
				if(timeout == false){
					BlockHelperLog("Retrying request for: "+rpc_endpoint+" due to error");
				}else{
					break; //We won't retry because timeout was reached
				}
				request_times++;
			}
			return responseString;
		}
		
		public static Byte[] Uint2Bytes(uint myint)
		{
			//Converts the int to bytes and make sure its little endian
			Byte[] mybyte = BitConverter.GetBytes(myint);
			if(BitConverter.IsLittleEndian == false){
				//Convert it to little endian
				Array.Reverse(mybyte);
			}
			return mybyte;
		}
		
		public static uint Bytes2Uint(Byte[] mybyte)
		{
			//Convert the 4 bytes into the uint
			if(BitConverter.IsLittleEndian == false){
				//Convert it to big endian before converting
				Array.Reverse(mybyte);
			}			
			uint myint = BitConverter.ToUInt32(mybyte,0);
			return myint;
		}		
	}
	
}