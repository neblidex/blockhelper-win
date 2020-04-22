/*
 * Created by SharpDevelop.
 * User: David4Neblio
 * Date: 2/19/2019
 * Time: 8:16 PM
 * License: MIT
 * 
 * To change this template use Tools | Options | Coding | Edit Standard Headers.
 */
 
 //The NebliDex BlockHelper is a helper program that connects to NeblioD RPC to obtain and index transaction information
 //This will be used by NebliDex critical nodes to avoid connecting to the API unless no other option
 //Normal nodes will query the Critical Nodes if the API is down
 //Functionality:
 //- Get unspent tx outs (UTXOUTs) with token amounts. Formatted to match API
 //- Get spent tx outs with token amounts. Formatted to match API. Used for eventual atomic swaps
 //- Get transaction information with token amounts. Formatted to match API
 //- Send token functionality with OP_Return info.
 //- Broadcast transaction info, with tx hash return
 //- Auxillary functions to ParseNTP1 scripts and CreateNTP1 scripts
 //- Supports Reorg with call back to specific blocks and checks blockhashes to make sure they haven't changed
 //- Changes in JSON to match API: Convert amounts to Satoshi, add txid, blockheight and change n to index in UTXO
using System;
using System.Data.SQLite;
using Newtonsoft.Json;
using System.IO;
using Newtonsoft.Json.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections;
using System.Globalization;
using System.Text;

namespace BlockHelper
{
	public partial class Program
	{
		public static string App_Path = AppDomain.CurrentDomain.BaseDirectory;
		public static string version_text = "1.0.1"; //First version of NebliDex Helper
		public static int database_version = 1;
		public static FileStream lockfile = null;
		public static EventWaitHandle exit_event;
		public static bool exit_program = false;
		public static Timer program_timer;
		public static Timer checker_timer;
		public static int rpc_call_id = 0;
		
		//BlockHelper Configurables
		public static int blockchain_blocktime = 30; //30 second blocktimes for Neblio, determines period of checking blockchain
		public static int blockhelper_port = 6327; //This is the listening port for the server of neblidex blockhelper
		public static int blockhelper_batchsize = 2000; //The size of blocks committed to memory at a time
		
		//Create the classes for the data, we will store data in memory and batch write to the database per block
		public class Block
		{
			public long blocknum;
			public string blockhash = "";
			public int blocktime;
			public List<BlockTransaction> blocktx_list;
			public Block(){
				blocktx_list = new List<BlockTransaction>();
			}
		}
		
		public class BlockTransaction
		{
			public long blocknum;
			public string blockhash;
			public string transactionhash;
			public string encoded_string;
			public int vin_count;
			public int vout_count;
			public List<UTXO> utxo_list; //A list of UTXOs created by the transaction and UTXOs used by transaction
			public BlockTransaction(){
				utxo_list = new List<UTXO>();
			}
		}
	
		public class UTXO
		{
			public bool spending = false; //If true, we are spending a UTXO from a previous transaction
			public string transactionhash; //This is the hash associated with the transaction that created the UTXO, not the spending tx
			public int vout_index;
			public string neb_address; //Will be empty if spending is true
			public string encoded_string; //Will be empty if spending is true
			public string spendsig; //Will be emtpy if spending is false
		}
		
		public static void Main(string[] args)
		{	
			Console.WriteLine("NebliDex BlockHelper RPC Service v"+version_text+" Starting...");
			LoadDatabase();
			ConnectRPC();
			
			//Now set up the while loop and run most of the code in an alternative thread
			exit_event = new EventWaitHandle(false,EventResetMode.AutoReset);
			
			program_timer = new Timer(new TimerCallback(RunBlockHelper),null,1000,Timeout.Infinite);
			
			while(exit_program == false){
				exit_event.WaitOne(); //This program will wait till signaled to exit
				exit_event.Reset(); //Reset the exit event
			}
			
			Close_Program();
		}
		
		public static void ConnectRPC()
		{
			Console.WriteLine("Neblio RPC credentials are required before use");
			Console.WriteLine("Please enter your nebliod rpc username (leave blank for default = nebliorpc)");
			rpc_username = Console.ReadLine();
			if(rpc_username.Trim().Length == 0){
				rpc_username = "nebliorpc";
			}
			Console.WriteLine("Please enter your nebliod rpc password (leave blank for default = admin)");
			rpc_pass = Console.ReadLine();
			if(rpc_pass.Trim().Length == 0){
				rpc_pass = "admin";
			}	
			Console.WriteLine("Please enter your nebliod rpc port (leave blank for default = 6326)");
			string port_string = Console.ReadLine();
			if(port_string.Trim().Length > 0){
				rpc_port = Convert.ToInt32(port_string);
			}

			//Now try to connect to the local node
			Console.Clear();
			Console.WriteLine("NebliDex BlockHelper RPC Service v"+version_text);
			Console.WriteLine("Attempting to connect to local nebliod node using provided credentials");
					
			//Try to query
			bool connected = false;
			bool synced = DaemonSynced(out connected); //This will check to make sure the daemon is synced or connected
			if(synced == false){
				if(connected == true){
					Console.WriteLine("Nebliod is not synced. Please re-open NebliDex BlockHelper RPC after nebliod is fully synced.");
					Close_Program();
				}else{
					Console.WriteLine("Unable to connect to nebliod. Please make sure nebliod is running and the port is available.");
					Close_Program();					
				}
			}
		}
		
		//Method to run BlockHelper
		public static void RunBlockHelper(object state)
		{
			
			//First get the latest blockheight from nebliod
			long rpc_blockheight = GetRPCBlockHeight();
			if(rpc_blockheight == -1){
				//NeblioD not synced, exit program
				ExitBlockHelperThread();
				return;
			}
			
			Console.WriteLine("Checking Blockchain Consistency");
			int blocks_back = FullCheckBlockchainReorg();
			if(blocks_back > 0){
				//In most cases, this should be 0
				Console.WriteLine("Reorg detected since last run, rolling back blockchain "+blocks_back+" blocks");
				BlockHelperLog("Blockchain reorg has occurred, rolling back "+blocks_back+" block(s)");
				RollbackDatabase(blocks_back);
			}
			
			long db_blockheight = GetDatabaseBlockHeight();
			
			if(db_blockheight < rpc_blockheight){
				//Sync the database up to the RPC
				SQLiteConnection mycon = ConnectDB();
				long batch_pos = 0;
				
				//Start a transaction and separate them by batches
				string myquery = "BEGIN TRANSACTION";
				SQLiteCommand statement = new SQLiteCommand(myquery,mycon); //Create a transaction to make inserts faster
				statement.ExecuteNonQuery();
				statement.Dispose();
				Console.Clear();
				Console.WriteLine("NebliDex BlockHelper RPC Service v"+version_text);
				Console.WriteLine("NebliDex BlockHelper is catching up database index to nebliod");
				Console.WriteLine("Do not connect to BlockHelper until it is fully synced");
				
				int startsync_time = UTCTime();
			
				for(long currentheight = db_blockheight+1;currentheight <= rpc_blockheight;){
					//Go through each block 1 by 1 until we complete the rpc_blockheight
					//It is expected that while indexing, the rpc_blockheight will increase slowly, after initial
					//sync, BlockHelper will continue to stay up to date in periodic intervals (based on blocktime)

					//We are going to query the RPC server in batches to speed up the process
					long start_height = currentheight;
					long rpc_batch_size = 100; //Ideal size for writing
					if(start_height+rpc_batch_size >= rpc_blockheight){
						rpc_batch_size = rpc_blockheight-start_height+1;
					}
					
					//Display progress between read batches
					//if(batch_pos % 50 == 0){
					Decimal percent_done = 0;
					if(rpc_blockheight > 0){
						percent_done = Math.Round(Convert.ToDecimal(currentheight)/Convert.ToDecimal(rpc_blockheight)*100,2);
					}
					ConsoleClearLine();
					Console.Write("Blocks processed: "+currentheight+"/"+rpc_blockheight+" ("+percent_done+"%)");
					//}

					Dictionary<long,Block> blocklist = GetRPCBlockDetailsBatch(start_height,rpc_batch_size);
					if(blocklist == null){
						BlockHelperLog("Unable to obtain information on block batch "+currentheight+", retrying");
						continue; //This just tries again
					}
					if(blocklist.Count != rpc_batch_size){
						BlockHelperLog("Unable to obtain information on block batch "+currentheight+", retrying");
						continue;
					}
					
					while(rpc_batch_size > 0){
						//Go through the batches of blocks that are present and save them to the database in order
						Block bk = blocklist[currentheight];
						
						//Now insert the changes to the database
						bool ok = AddBlockToDatabase(bk,mycon);
						if(ok == false){
							mycon.Dispose();
							throw new Exception("Failed to add block "+currentheight+" to database");
							//Database errors are designed to crash the program
						}
						
						if(currentheight == rpc_blockheight){
							BlockHelperLog("Current blockheight: "+rpc_blockheight+", hash: "+bk.blockhash);
						}
						
						//Writing in batches makes the process faster
						batch_pos++;
						if(batch_pos >= blockhelper_batchsize){
							myquery = "COMMIT TRANSACTION";
							statement = new SQLiteCommand(myquery,mycon); //Create a transaction to make inserts faster
							statement.ExecuteNonQuery();
							statement.Dispose();
							myquery = "BEGIN TRANSACTION";
							statement = new SQLiteCommand(myquery,mycon); //Create a transaction to make inserts faster
							statement.ExecuteNonQuery();
							statement.Dispose();
							batch_pos = 0;						
						}
						currentheight++;
						rpc_batch_size--;
					}
				}
				
				//Commit whatever changes have been made in the transaction
				myquery = "COMMIT TRANSACTION";
				statement = new SQLiteCommand(myquery,mycon); //Create a transaction to make inserts faster
				statement.ExecuteNonQuery();
				statement.Dispose();
				mycon.Dispose();
				
				//Write to Log how log it took to sync
				BlockHelperLog("Completed sync to nebliod in "+(UTCTime()-startsync_time)+" seconds");
			}
			
			//Now turn on the server to listen to NebliDex clients and start a timer for blockchain syncing
			SetServer(true);
			Console.Clear();
			Console.WriteLine("NebliDex BlockHelper RPC Service v"+version_text);
			Console.WriteLine("NebliDex BlockHelper is fully synced. Server is now active at port "+blockhelper_port);
			Console.WriteLine("Current Block Height: "+rpc_blockheight);
			
			//Now run the periodic checker, starting now
			checker_timer = new Timer(new TimerCallback(RunBlockHelperCheck),null,0,Timeout.Infinite);
		}
		
		public static void RunBlockHelperCheck(object state)
		{
			//This code is ran periodically and will check the blockheight of nebliod
			//Along with specific blockheights/hashes and compare the hashes to nebliod to check for blockchain reorganization
			//If blockchain reorganization occurs, blockhelper must rollback to the point of the reorganization and re-index
			int method_time = UTCTime();
			
			CheckServerConnections(); //Check the connections to this server and disconnect if necessary
			
			bool connected;
			bool synced = DaemonSynced(out connected);
			bool skipcheck = false;
			if(synced == false){
				if(connected == false){
					//Unable to connect to nebliod
					Console.Clear();
					Console.WriteLine("NebliDex BlockHelper RPC Service v"+version_text);
					Console.WriteLine("NebliDex BlockHelper is not connected to nebliod. Check nebliod.");	
				}else{
					//Nebliod not synced to blockchain network
					Console.Clear();
					Console.WriteLine("NebliDex BlockHelper RPC Service v"+version_text);
					Console.WriteLine("NebliDex BlockHelper is not synced to blockchain network. Nebliod is not synced to network.");	
				}
				if(server_active == true){
					SetServer(false); //Disconnect the server for now
				}
				skipcheck = true;
			}
			
			try{
				if(skipcheck == false){
					//First check for blockchain reorg
					int blocks_back = SpotCheckBlockchainReorg();
					if(blocks_back > 0){
						//In most cases, this should be 0
						BlockHelperLog("Blockchain reorg has occurred, rolling back "+blocks_back+" block(s)");
						RollbackDatabase(blocks_back);
					}
					
					//Now update the blockchain index
					long rpc_blockheight = GetRPCBlockHeight();
					if(rpc_blockheight > -1){
						long db_blockheight = GetDatabaseBlockHeight();
						if(db_blockheight == -1){
							throw new Exception("Failed to read database, unusual exception");
						}
						//This is expected to be at least one block behind rpc_blockheight
						if(db_blockheight < rpc_blockheight){
							//Sync the database up to the RPC
							SQLiteConnection mycon = ConnectDB();
							long batch_pos = 0;
							
							//Start a transaction and separate them by batches
							string myquery = "BEGIN TRANSACTION";
							SQLiteCommand statement = new SQLiteCommand(myquery,mycon); //Create a transaction to make inserts faster
							statement.ExecuteNonQuery();
							statement.Dispose();			
							for(long currentheight = db_blockheight+1;currentheight <= rpc_blockheight;currentheight++){
								//Go through each block 1 by 1 until we complete the rpc_blockheight
								//It is expected that while indexing, the rpc_blockheight will increase slowly, after initial
								//sync, BlockHelper will continue to stay up to date in periodic intervals (based on blocktime)
	
								Block bk = new Block();
								bk.blocknum = currentheight;
								GetRPCBlockDetails(bk); //Fill this database with information
								if(bk.blockhash.Length == 0){
									BlockHelperLog("Unable to obtain information on block "+currentheight+", retrying");
									currentheight--; //Retry at the same height
									continue;
								}
								
								//Now insert the changes to the database
								bool ok = AddBlockToDatabase(bk,mycon);
								if(ok == false){
									mycon.Dispose();
									throw new Exception("Failed to add block "+currentheight+" to database");
									//Database errors crash the program
								}
								
								if(currentheight == rpc_blockheight){
									BlockHelperLog("Current blockheight: "+rpc_blockheight+", hash: "+bk.blockhash);
								}
								
								//Doing this is batches makes the process faster
								batch_pos++;
								if(batch_pos >= blockhelper_batchsize){
									myquery = "COMMIT TRANSACTION";
									statement = new SQLiteCommand(myquery,mycon); //Create a transaction to make inserts faster
									statement.ExecuteNonQuery();
									statement.Dispose();
									myquery = "BEGIN TRANSACTION";
									statement = new SQLiteCommand(myquery,mycon); //Create a transaction to make inserts faster
									statement.ExecuteNonQuery();
									statement.Dispose();
									batch_pos = 0;						
								}
							}
							
							//Commit whatever changes have been made in the transaction
							myquery = "COMMIT TRANSACTION";
							statement = new SQLiteCommand(myquery,mycon); //Create a transaction to make inserts faster
							statement.ExecuteNonQuery();
							statement.Dispose();
							mycon.Dispose();
							
							Console.Clear();
							Console.WriteLine("NebliDex BlockHelper RPC Service v"+version_text);
							Console.WriteLine("NebliDex BlockHelper is currently synced. Server port: "+blockhelper_port);	
							Console.WriteLine("Current Block Height: "+rpc_blockheight);				
						}
					}
					
					if(server_active == false){
						SetServer(true);
					}
					
				}
			}catch(Exception e){
				BlockHelperLog("Exception thrown in checker method, error: "+e.ToString());
			}
			
			int diff_time = UTCTime() - method_time; //The time this method took in seconds
			int sec_remain = blockchain_blocktime - diff_time;
			if(sec_remain < 0){sec_remain = 0;}
			checker_timer.Change(sec_remain*1000,System.Threading.Timeout.Infinite); //Run again soon
		}
		
		public static void ExitBlockHelperThread()
		{
			exit_program = true;
			exit_event.Set();			
		}
		
		//RPC calls
		public static long GetRPCBlockHeight()
		{
			try{
				JObject js = new JObject();
				js["id"] = 1;
				js["method"] = "getblockcount";
				js["params"] = new JArray(); //Empty array
				string json_encoded = JsonConvert.SerializeObject(js);
				bool timeout = false;
				string resp = RPCRequest(json_encoded,out timeout);
				if(resp.Length == 0 || timeout == true){
					return -1; //Unable to connect
				}
				js = JObject.Parse(resp);
				if(js["result"] != null){
					return Convert.ToInt64(js["result"].ToString());
				}
			}catch(Exception e){
				BlockHelperLog("Network error: "+e.ToString());
			}
			return -1;
		}
		
		public static string BroadcastRPCTransaction(string tx_hex)
		{
			try{
				JObject js = new JObject();
				js["id"] = 1;
				js["method"] = "sendrawtransaction";
				js["params"] = new JArray(tx_hex);
				string json_encoded = JsonConvert.SerializeObject(js);
				bool timeout = false;
				string resp = RPCRequest(json_encoded,out timeout);
				if(resp.Length == 0 || timeout == true){
					return ""; //Unable to connect
				}
				js = JObject.Parse(resp);
				if(js["result"] != null){
					return js["result"].ToString();
				}
			}catch(Exception e){
				BlockHelperLog("Network error: "+e.ToString());
			}
			return "";
		}
		
		public static string GetRPCBlockHash(long blocknum)
		{
			try{
				JObject js = new JObject();
				js["id"] = 1;
				js["method"] = "getblockbynumber";
				js["params"] = new JArray(blocknum,false);
				string json_encoded = JsonConvert.SerializeObject(js);
				bool timeout = false;
				string resp = RPCRequest(json_encoded,out timeout);
				if(resp.Length == 0 || timeout == true){
					throw new Exception("Failed to connect to RPC node");
				}
				js = JObject.Parse(resp);
				if(js["result"] == null){
					throw new Exception("Failed to get result from RPC node, error: "+js["error"].ToString());
				}
				js = (JObject)js["result"]; //We only want the result information
				return js["hash"].ToString();
			}catch(Exception e){
				BlockHelperLog("Failed to grab blockhash for block "+blocknum+", error: "+e.ToString());
			}
			return "";
		}
		
		public static string CreateRPCNTP1Transaction(JArray txins,string targets)
		{
			try{
				JObject js = new JObject();
				js["id"] = 1;
				js["method"] = "createrawntp1transaction";
				js["params"] = new JArray(txins,"placeholder");
				string json_encoded = JsonConvert.SerializeObject(js);
				json_encoded = json_encoded.Replace("\"placeholder\"",targets);
				
				bool timeout = false;
				string resp = RPCRequest(json_encoded,out timeout);
				if(resp.Length == 0 || timeout == true){
					throw new Exception("Failed to connect to RPC node");
				}
				js = JObject.Parse(resp);
				if(js["result"] == null){
					throw new Exception("Failed to get result from RPC node, error: "+js["error"].ToString());
				}
				return js["result"].ToString(); //Should be an encoded unsigned transaction hash
			}catch(Exception e){
				BlockHelperLog("Failed to create ntp1 transaction, error: "+e.ToString());
			}
			return "";
		}
		
		public static bool DaemonSynced(out bool connected)
		{
			//This will check to see if the daemon (nebliod) is synced with the blockchain network or even if online
			try{
				JObject js = new JObject();
				js["id"] = 1;
				js["method"] = "getstakinginfo";
				js["params"] = new JArray(); //Empty array
				string json_encoded = JsonConvert.SerializeObject(js);
				bool timeout = false;
				string resp = RPCRequest(json_encoded,out timeout);
				if(resp.Length == 0 || timeout == true){
					connected = false; //Unable to connect
					return false; //Unable to connect
				}
				js = JObject.Parse(resp);
				connected = true;
				if(js["result"] != null){
					//Only the alpha version of neblioqt will return this result
					return Convert.ToBoolean(js["result"]["staking-criteria"]["synced"].ToString());
				}
			}catch(Exception e){
				BlockHelperLog("Network error: "+e.ToString());
				Console.WriteLine("Error parsing Nebliod information. Please make sure you have latest version of Nebliod alpha builds.");
			}
			connected = false;
			return false;
		}
		
		public static Dictionary<long,Block> GetRPCBlockDetailsBatch(long blocknum,long amount)
		{
			//The blocknum + amount should be less than blockheight otherwise dictionary will return null
			try{
				JArray blocks_request = new JArray();
				//First request the hashes for the blocks
				for(long i = blocknum;i < blocknum+amount;i++){
					JObject js = new JObject();
					js["id"] = i.ToString(); //The ID represents the blockheight
					js["method"] = "getblockhash";
					js["params"] = new JArray(i);
					blocks_request.Add(js);
				}
				string json_encoded = JsonConvert.SerializeObject(blocks_request); //This will be a very large string
				bool timeout = false;
				string resp = RPCRequest(json_encoded,out timeout,120); //Wait at most 2 minutes for a response
				if(resp.Length == 0 || timeout == true){
					throw new Exception("Failed to connect to RPC node");
				}
				
				//Now we will use the return query to generate a new request for the block details
				JArray results_array = JArray.Parse(resp);
				blocks_request = new JArray();
				foreach (JObject result in results_array) {
					if(result["result"] == null){
						throw new Exception("Failed to get all results from RPC node, error: "+result["error"].ToString());
					}
					JObject js = new JObject();
					js["id"] = result["id"];
					js["method"] = "getblock";
					long num = Convert.ToInt64(result["id"].ToString());
					string hash = result["result"].ToString(); //This should be the blockhash
					if(num > 0){
						js["params"] = new JArray(hash,true,true); //This function will return block info including transaction info
					}else{
						js["params"] = new JArray(hash,true,false);
					}
					blocks_request.Add(js);
				}
				json_encoded = JsonConvert.SerializeObject(blocks_request); //This will be a very large string
				timeout = false;
				resp = RPCRequest(json_encoded,out timeout,120); //Wait at most 2 minutes for a response
				if(resp.Length == 0 || timeout == true){
					throw new Exception("Failed to connect to RPC node");
				}
				
				//The returned array may not be in order, so match per ID
				Dictionary<long,Block> blocklist = new Dictionary<long,Block>();
				results_array = JArray.Parse(resp);
				foreach (JObject result in results_array) {
					Block bk = new Block();
					if(result["result"] == null){
						throw new Exception("Failed to get all results from RPC node, error: "+result["error"].ToString());
					}
					long num = Convert.ToInt64(result["id"].ToString()); //The ID in this case represents the blocknum
					JObject js = (JObject)result["result"];
					bk.blocknum = num;
					FillBlockDetails(js,bk);
					blocklist.Add(num,bk);
				}
				return blocklist;
			}catch(Exception e){
				BlockHelperLog("Failed to grab batch of block details at block "+blocknum+", error: "+e.ToString());
			}
			return null;
		}
		
		public static void FillBlockDetails(JObject js,Block bk)
		{
			//This will fill the block in with details
			bk.blockhash = js["hash"].ToString();
			bk.blocktime = Convert.ToInt32(js["time"].ToString());
			if(bk.blocknum == 0){return;} //Genesis block has no good details
			
			//Now parse through the transaction information
			foreach (JToken txinfo in js["tx"]) {
				//Some blocks do not have transactions, some do
				BlockTransaction tx = new BlockTransaction();
				tx.blockhash = bk.blockhash;
				tx.vin_count = 0;
				tx.vout_count = 0;
				tx.blocknum = bk.blocknum;
				tx.transactionhash = txinfo["txid"].ToString();
				foreach (JToken vin in txinfo["vin"]) {
					if(vin["coinbase"] != null){
						continue; //Skip this vin if it is a coinbase vin
					}
					UTXO txvin = new UTXO();
					txvin.spending = true;
					txvin.transactionhash = vin["txid"].ToString();
					txvin.vout_index = Convert.ToInt32(vin["vout"].ToString());
					txvin.spendsig = vin["scriptSig"]["asm"].ToString();
					txvin.encoded_string = vin.ToString();
					tx.utxo_list.Add(txvin);
					tx.vin_count++;
				}
				foreach (JToken vout in txinfo["vout"]) {
					if(vout["scriptPubKey"] == null){ //No scriptpubkey
						continue;
					}
					string vout_type = vout["scriptPubKey"]["type"].ToString();
					if(vout_type == "nulldata" || vout_type == "nonstandard"){
						continue; //These can't be spent
					}
					JValue vout_val = (JValue)vout["value"]; //This is needed to prevent JSON auto convert
					ulong neb_value = Convert.ToUInt64(Decimal.Parse(vout_val.ToString(CultureInfo.InvariantCulture),System.Globalization.NumberStyles.Float,CultureInfo.InvariantCulture)*100000000); //Get the sat value
					if(neb_value == 0){
						continue; //Valid Vout but no value so do not store UTXO
					}
					vout["value"] = neb_value; //Convert the vout to satoshi amount (no decimal)
					if(vout["scriptPubKey"]["addresses"] == null){
						continue; //No destination addresses listed
					}
					string neb_address = "";
					foreach (JToken address in vout["scriptPubKey"]["addresses"]) {
						neb_address = address.ToString();
						break;
					}
					if(neb_address.Length == 0){
						continue; //No destination address
					}
					UTXO txvout = new UTXO();
					txvout.spending = false; //Created new coin
					txvout.transactionhash = tx.transactionhash;
					txvout.vout_index = Convert.ToInt32(vout["n"].ToString());
					txvout.neb_address = neb_address;
					txvout.encoded_string = JsonConvert.SerializeObject(vout);
					tx.utxo_list.Add(txvout);
					tx.vout_count++;
				}
				tx.encoded_string = JsonConvert.SerializeObject(txinfo);
				bk.blocktx_list.Add(tx); //Add transaction to blocklist
			}			
		}
		
		public static void GetRPCBlockDetails(Block bk)
		{
			//This function connects to the RPC and gets specific details about the block
			//We will perform two calls, getblockhash then with the hash, getblock
			//Per Neblio Dev, this is faster than one call to getblockbynum for an unknown reason
			string hash = GetRPCBlockHash(bk.blocknum);
			if(hash.Length == 0){return;} //No hash for blockheight
			try{
				JObject js = new JObject();
				js["id"] = 1;
				js["method"] = "getblock";
				if(bk.blocknum > 0){
					js["params"] = new JArray(hash,true,true); //This function will return block info including transaction info
				}else{
					js["params"] = new JArray(hash,true,false);
				}
				string json_encoded = JsonConvert.SerializeObject(js);
				bool timeout = false;
				string resp = RPCRequest(json_encoded,out timeout);
				if(resp.Length == 0 || timeout == true){
					throw new Exception("Failed to connect to RPC node");
				}
				js = JObject.Parse(resp);
				if(js["result"] == null){
					throw new Exception("Failed to get result from RPC node, error: "+js["error"].ToString());
				}
				js = (JObject)js["result"]; //We only want the result information
				FillBlockDetails(js,bk);			
			}catch(Exception e){
				throw new Exception("RPC call failed :"+e.ToString());
			}			
		}
		
		public static void LoadDatabase()
		{
			//This function creates the databases and loads the user data
			//Anything that gets deleted when user closes program is not stored in database
			
			//First check if data folder exists
			if(Directory.Exists(App_Path+"/blockdata") == false){
				//Folder does not exist, create the path
				Directory.CreateDirectory(App_Path+"/blockdata");
			}
			
			//Try to acquire a file lock to prevent other instances to open while NebliDex is running
			try {
				lockfile = new FileStream(App_Path+"/blockdata/lock.file",FileMode.OpenOrCreate,FileAccess.Read,FileShare.None);
			} catch (Exception) {
				//Ran sync
				Console.WriteLine("An instance of NebliDex Block Helper is already open");
				Close_Program();
			}
			
			if(File.Exists(App_Path+"/blockdata/debug.log") == true){
				long filelength = new System.IO.FileInfo(App_Path+"/blockdata/debug.log").Length;
				if(filelength > 10000000){ //Debug log is greater than 10MB
					lock(debugfileLock){
						File.Delete(App_Path+"/blockdata/debug.log"); //Clear the old log
					}
				}
			}
			
			BlockHelperLog("Loading new instance of NebliDex Block Helper version: "+version_text);
			
			SetupExceptionHandlers();	

			if(File.Exists(App_Path+"/blockdata/blockindex.db") == false){
				
				BlockHelperLog("Creating databases");
				SQLiteConnection.CreateFile(App_Path+"/blockdata/blockindex.db");
				//Now create the tables
				string myquery;
				SQLiteConnection mycon = new SQLiteConnection("Data Source=\""+App_Path+"blockdata/blockindex.db\";Version=3;");
				mycon.Open();
				
				//Create My Tradehistory table
				myquery = "Create Table BLOCK";
				myquery += " (nindex Integer Primary Key ASC, blocknum Integer, blockhash Text, blocktime Integer)";
				//Blockhash will be indexed
				//Blocknum will be indexed
				//If a block is not fully processed, it must be rolled back and all its transactions removed
				SQLiteCommand statement = new SQLiteCommand(myquery,mycon);
				statement.ExecuteNonQuery();
				statement.Dispose();
				
				//Create Transaction table
				myquery = "Create Table BLOCKTRANSACTION";
				myquery += " (nindex Integer Primary Key ASC, blocknum Integer, blockhash Text, transactionhash Text, encoded_string Text, vin_count Integer, vout_count Integer)";
				//Blockhash and transactionhash are indexed separately
				statement = new SQLiteCommand(myquery,mycon);
				statement.ExecuteNonQuery();
				statement.Dispose();
				
				//Create UTXO table
				myquery = "Create Table UTXO";
				myquery += " (nindex Integer Primary Key ASC, transactionhash Text, vout_index Integer, neb_address Text, encoded_string Text, spent Integer, spendsig Text)";
				//Neblio address and spent are Indexed together while transactionhash is indexed separately
				statement = new SQLiteCommand(myquery,mycon);
				statement.ExecuteNonQuery();
				statement.Dispose();			
				
				//Create a table for version control
				myquery = "Create Table FILEVERSION (nindex Integer Primary Key ASC, version Integer)";
				statement = new SQLiteCommand(myquery,mycon);
				statement.ExecuteNonQuery();
				statement.Dispose();
				//Insert a row with version
				myquery = "Insert Into FILEVERSION (version) Values ("+database_version+");";
				statement = new SQLiteCommand(myquery,mycon);
				statement.ExecuteNonQuery();
				statement.Dispose();
				
				//Now create the indices for the tables
				myquery = "Create Index index1 On BLOCK(blocknum);Create Index index2 On BLOCK(blockhash)";
				statement = new SQLiteCommand(myquery,mycon);
				statement.ExecuteNonQuery();
				statement.Dispose();

				myquery = "Create Index index3 On BLOCKTRANSACTION(blockhash);Create Index index4 On BLOCKTRANSACTION(transactionhash)";
				statement = new SQLiteCommand(myquery,mycon);
				statement.ExecuteNonQuery();
				statement.Dispose();

				myquery = "Create Index index5 On UTXO(transactionhash);Create Index index6 On UTXO(neb_address,spent)";
				statement = new SQLiteCommand(myquery,mycon);
				statement.ExecuteNonQuery();
				statement.Dispose();				
								
				mycon.Dispose();
			}			
		}
		
		public static SQLiteConnection ConnectDB()
		{
			SQLiteConnection mycon = new SQLiteConnection("Data Source=\""+App_Path+"blockdata/blockindex.db\";Version=3;");
			mycon.Open();

			//Set our busy timeout, so we wait if there are locks present
			SQLiteCommand statement = new SQLiteCommand("PRAGMA busy_timeout = 5000",mycon); //Create a transaction to make inserts faster
			statement.ExecuteNonQuery();
			statement.Dispose();

			return mycon;			
		}
		
		public static int FullCheckBlockchainReorg()
		{
			BlockHelperLog("Checking Blockchain Full Reorg");
			//This is checked at the beginning of the program for blockchain reorgs. Neblio has tendency to reorg recent blocks a lot
			//Check a quarter days worth of reorgs
			long blocks_to_check = (long)Math.Round((double)86400 / (double)(blockchain_blocktime*4));
			long current_height = GetDatabaseBlockHeight();
			if(current_height == -1){return 0;}
			
			long start_block = current_height - blocks_to_check;
			if(start_block < 0){start_block = 0;}

			SQLiteConnection mycon = ConnectDB();
			string myquery = "Select blockhash From BLOCK Where blocknum = @bnum";
			SQLiteCommand statement = new SQLiteCommand(myquery,mycon);
			int blocks_back = 0;
			for(long i = start_block;i <= current_height;i++){
				statement.Parameters.AddWithValue("@bnum",i);
				SQLiteDataReader statement_reader = statement.ExecuteReader();
				statement_reader.Read();
				
				string db_blockhash = statement_reader["blockhash"].ToString();
				string rpc_blockhash = GetRPCBlockHash(i);

				statement_reader.Dispose();
				statement.Parameters.Clear();
				
				if(rpc_blockhash.Length > 0){
					//Data was returned, compare them
					if(rpc_blockhash != db_blockhash){
						//The block hash at this block height is different, reorg has occurred
						//We need to rollback before the reorg has occurred
						BlockHelperLog("Chain split at blockheight: "+i+", expected hash: "+db_blockhash+", returned hash: "+rpc_blockhash);
						blocks_back = Convert.ToInt32(current_height - i)+1;
						break;
					}
				}
			}
			statement.Dispose();
			mycon.Dispose();
			BlockHelperLog("Finished checking Blockchain Full Reorg");
			return blocks_back;			
		}
		
		public static int SpotCheckBlockchainReorg()
		{
			//This function will check specific blocks in the chain and see if they have changed, if so, a reorg has occurred and the database
			//needs to be rollbacked. It checks 0,1,2,3,4,10,20,50,100 blocks back in order
			//Neblio tends to have quite a bit of reorgs for some reason
			//The method returns the amount of blocks to rollback
			long current_height = GetDatabaseBlockHeight();
			if(current_height == -1){return 0;}			
			
			long[] target_blocks = new long[9];
			target_blocks[0] = current_height;
			target_blocks[1] = current_height - 1;
			target_blocks[2] = current_height - 2;
			target_blocks[3] = current_height - 3;
			target_blocks[4] = current_height - 4;
			target_blocks[5] = current_height - 10;
			target_blocks[6] = current_height - 20;
			target_blocks[7] = current_height - 50;
			target_blocks[8] = current_height - 100;
			if(target_blocks[8] < 0){return 0;}
			
			long fork_block = -1;
			
			SQLiteConnection mycon = ConnectDB();
			string myquery = "Select blockhash From BLOCK Where blocknum = @bnum";
			SQLiteCommand statement = new SQLiteCommand(myquery,mycon);
			int blocks_back = 0;
			for(int i = 0;i < target_blocks.Length;i++){
				statement.Parameters.AddWithValue("@bnum",target_blocks[i]);
				SQLiteDataReader statement_reader = statement.ExecuteReader();
				statement_reader.Read();
				
				string db_blockhash = statement_reader["blockhash"].ToString();
				string rpc_blockhash = GetRPCBlockHash(target_blocks[i]);

				statement_reader.Dispose();
				statement.Parameters.Clear();
				
				if(rpc_blockhash.Length > 0){
					//Data was returned, compare them
					if(rpc_blockhash != db_blockhash){
						//The block hash at this block height is different, reorg has occurred
						//We need to rollback before the reorg has occurred
						BlockHelperLog("Chain split at blockheight: "+target_blocks[i]+", expected hash: "+db_blockhash+", returned hash: "+rpc_blockhash);
						fork_block = target_blocks[i];
						blocks_back = Convert.ToInt32(current_height - target_blocks[i])+1;
					}else if(fork_block > -1){
						//This block is in sync, the fork occurred between this block and the fork_block
						//We need to rollback to at least this block
						blocks_back = Convert.ToInt32(current_height - target_blocks[i]);
						break;
					}else{
						//If the most recent block is synced then all the prior blocks are too
						break;
					}
				}
			}
			statement.Dispose();
			mycon.Dispose();
			return blocks_back;
		}
		
		public static long GetDatabaseBlockHeight()
		{
			//This function will return the blockheight of database, -1 if no blocks exist
			long blockheight = -1; //Database may not have blockdata
			try{
				using(SQLiteConnection mycon = ConnectDB()){
					string myquery = "Select blocknum,blockhash From BLOCK Order By nindex DESC Limit 1";
					using(SQLiteCommand statement = new SQLiteCommand(myquery,mycon)){
						using(SQLiteDataReader statement_reader = statement.ExecuteReader()){
							bool dataavail = statement_reader.Read();
							if(dataavail){
								blockheight = Convert.ToInt64(statement_reader["blocknum"].ToString());
								string blockhash = statement_reader["blockhash"].ToString();
							}
						}
					}
				}
			}catch(Exception e){
				BlockHelperLog("Failed to grab blockheight data from database, error: "+e.ToString());
			}
			return blockheight; //It will begin to process block 0 genesis block
		}
		
		public static JObject GetTxJSONFromDatabase(string tx_hash)
		{
			//Grab the transaction information
			long blockheight = GetDatabaseBlockHeight();
			if(blockheight == -1){return null;}
			
			SQLiteConnection mycon = ConnectDB();
			JObject tx_json = null;
			try {
				string myquery = "Select blocknum, encoded_string From BLOCKTRANSACTION Where transactionhash = @thash";
				using(SQLiteCommand statement = new SQLiteCommand(myquery,mycon)){
					statement.Parameters.AddWithValue("@thash",tx_hash);
					using(SQLiteDataReader statement_reader = statement.ExecuteReader()){
						bool dataavail = statement_reader.Read(); //Transaction may not be there
						if(dataavail){
							long blocknum = Convert.ToInt64(statement_reader["blocknum"].ToString());
							long confirmations = blockheight - blocknum + 1; //There is already one confirmation if the transaction is in block
							if(blocknum > 0){
								tx_json = JObject.Parse(statement_reader["encoded_string"].ToString());
								tx_json["confirmations"] = confirmations; //Update the amount of confirmations field
							}
						}
					}
				}			
			} catch (Exception e) {
				BlockHelperLog("Invalid data queried from SQLite database, error: "+e.ToString());
			}
			mycon.Dispose();
			return tx_json;			
		}
		
		public static JArray GetUnspentUTXOJSONFromDatabase(string nebadd)
		{
			//Get the unspent UTXOs for this particular neblio address
			JArray utxos = new JArray();
			SQLiteConnection mycon = ConnectDB();
			
			try{
				string myquery = "Select transactionhash, encoded_string From UTXO Where neb_address = @nadd And spent = 0";
				using(SQLiteCommand statement = new SQLiteCommand(myquery,mycon)){ //Prepare the UTXO command
					myquery = "Select blocknum From BLOCKTRANSACTION Where transactionhash = @thash";
					using(SQLiteCommand statement2 = new SQLiteCommand(myquery,mycon)){ //Prepare the Transaction command
						statement.Parameters.AddWithValue("@nadd",nebadd);
						using(SQLiteDataReader statement_reader = statement.ExecuteReader()){
							while(statement_reader.Read()){
								//Find the unspent UTXO, there may not be any
								JObject utxo = JObject.Parse(statement_reader["encoded_string"].ToString()); //Convert the string to a JObject
								utxo["index"] = utxo["n"];
								utxo["txid"] = statement_reader["transactionhash"].ToString();
								utxo.Remove("n"); //Remove the N field
								//Now search the database again for the transaction matching
								statement2.Parameters.AddWithValue("@thash",statement_reader["transactionhash"].ToString());
								using(SQLiteDataReader statement_reader2 = statement2.ExecuteReader()){
									bool dataavail = statement_reader2.Read();
									if(dataavail == true){
										//Should always be true unless reorg occurs while reading
										utxo["blockheight"] = Convert.ToInt64(statement_reader2["blocknum"].ToString());
										utxos.Add(utxo); //Add this UTXO to the overall unspent utxos
										statement_reader2.Dispose();
										statement2.Parameters.Clear();
									}else{
										statement_reader2.Dispose();
										statement2.Parameters.Clear();
										continue;
									}
								}
							}
						}
					}
				}
			}catch(Exception e){
				BlockHelperLog("Invalid data queried from SQLite database, error: "+e.ToString());
			}finally{
				mycon.Dispose();
			}			
			if(utxos.Count > 0){
				return utxos;
			}else{
				return null;
			}
		}
		
		public static JArray GetSpentUTXOJSONFromDatabase(string nebadd,int num_utxo)
		{
			//Get the most recently spent UTXOs for this particular neblio address
			//This will be used to perform atomic swaps when that is implemented by analyzing the spendsigs
			JArray utxos = new JArray();
			SQLiteConnection mycon = ConnectDB();
			
			try{
				string myquery = "Select transactionhash, encoded_string, spendsig From UTXO Where neb_address = @nadd And spent = 1 Order By nindex DESC Limit "+num_utxo;
				using(SQLiteCommand statement = new SQLiteCommand(myquery,mycon)){ //Prepare the UTXO command
					myquery = "Select blocknum From BLOCKTRANSACTION Where transactionhash = @thash";
					using(SQLiteCommand statement2 = new SQLiteCommand(myquery,mycon)){ //Prepare the Transaction command
						statement.Parameters.AddWithValue("@nadd",nebadd);
						using(SQLiteDataReader statement_reader = statement.ExecuteReader()){
							while(statement_reader.Read()){
								//Find the unspent UTXO, there may not be any
								JObject utxo = JObject.Parse(statement_reader["encoded_string"].ToString()); //Convert the string to a JObject
								utxo["index"] = utxo["n"];
								utxo["txid"] = statement_reader["transactionhash"].ToString();
								utxo.Remove("n"); //Remove the N field
								utxo["spendsig"] = statement_reader["spendsig"].ToString();
								//Now search the database again for the transaction matching
								statement2.Parameters.AddWithValue("@thash",statement_reader["transactionhash"].ToString());
								using(SQLiteDataReader statement_reader2 = statement2.ExecuteReader()){
									bool dataavail = statement_reader2.Read();
									if(dataavail == true){
										//Should always be true
										utxo["blockheight"] = Convert.ToInt64(statement_reader2["blocknum"].ToString());
										utxos.Add(utxo); //Add this UTXO to the overall spent utxos
										statement_reader2.Dispose();
										statement2.Parameters.Clear();
									}else{
										statement_reader2.Dispose();
										statement2.Parameters.Clear();
										continue;
									}
								}
							}
						}
					}
				}
			}catch(Exception e){
				BlockHelperLog("Invalid data queried from SQLite database, error: "+e.ToString());
			}
			mycon.Dispose();
			if(utxos.Count > 0){
				return utxos;
			}else{
				return null;
			}
		}
		
		public static JObject CreateScratchNTP1Transaction(string from_add,JArray targets)
		{
			//This will create the elements of a NTP1 transaction without using the RPC
			//The returned Jarray is an ordered list of all usedutxo, and a list of vout locations (index)
			//amounts, tokenid and addresses with the last vout being the opreturn with scriptsig
			
			List<string> token_types = new List<string>();
			foreach (JObject target in targets) {
				string unique_id = target["tokenId"].ToString();
				bool exist = false;
				for(int i = 0;i < token_types.Count;i++){
					if(unique_id == token_types[i]){
						exist = true;break; //We already have this token type in our list
					}
				}
				if(exist == false){
					//Add this new token type to our list
					if(unique_id.Trim().Length == 0){return null;} //No tokenID
					token_types.Add(unique_id);
				}
			}
			
			if(token_types.Count < 1){return null;} //Nothing to send
			
			//Now we have a list of unique tokens, we must follow the order for this to work correctly
			JArray unordered_utxos = GetUnspentUTXOJSONFromDatabase(from_add);
			if(unordered_utxos == null){return null;} //There are no unspent utxos
			
			Dictionary<string,long> tokeninput_amounts = new Dictionary<string, long>();
			Dictionary<string,long> tokenoutput_amounts = new Dictionary<string, long>();
			
			JArray ordered_utxos = new JArray();
			JArray ordered_targets = new JArray();
			for(int i = 0;i < token_types.Count;i++){
				bool match = false;
				foreach (JObject utxo in unordered_utxos) {
                    bool token_present = false;
                    int token_count = 0;
                    foreach (JObject token in utxo["tokens"])
                    {
                        //Go through the list of tokens in this UTXO, may have duplicate tokens
                        if (token["tokenId"].ToString() == token_types[i])
                        {
                            token_present = true;
                            match = true;
                            if (tokeninput_amounts.ContainsKey(token_types[i]) == false)
                            {
                                tokeninput_amounts[token_types[i]] = Convert.ToInt64(token["amount"].ToString());
                            }
                            else
                            {
                                tokeninput_amounts[token_types[i]] += Convert.ToInt64(token["amount"].ToString()); //Get the amount of this token type
                            }
                        }
                        token_count++;
                    }
                    if (token_present == true)
                    {
                        if (token_count > 1)
                        {
                            // More than 1 token at this UTXO, check whether they are mixed tokens (shouldn't happen normally)
                            string firstToken = "";
                            foreach (JObject token in utxo["tokens"])
                            {
                                if (firstToken.Length == 0)
                                {
                                    firstToken = token["tokenId"].ToString();
                                }
                                else
                                {
                                    if (firstToken.Equals(token["tokenId"].ToString()) == false)
                                    {                                    
                                        return null;
                                    }
                                }
                            }
                        }
                        ordered_utxos.Add(utxo); // Our desired token is in UTXO, add the UTXO in order
                    }
				}
				if(match == false){
					return null; //This means for this token type, it couldn't find a corresponding UTXO
				}				
				foreach (JObject target in targets) {
					if(target["tokenId"].ToString() == token_types[i]){
						if(tokenoutput_amounts.ContainsKey(token_types[i]) == false){
							tokenoutput_amounts[token_types[i]] = Convert.ToInt64(target["amount"].ToString()); 
						}else{
							tokenoutput_amounts[token_types[i]] += Convert.ToInt64(target["amount"].ToString()); //Get the amount of this token type
						}
						ordered_targets.Add(target);
					}
				}
				//By the end of this, we should have an ordered list of utxos based on order of token types sorted by first tokenid
				//We should also have an ordered list of targets sorted by tokenid
			}
			
			if(ordered_utxos.Count < 1){return null;} //There are no matching UTXOs for the token type
			
			for(int i = 0;i < token_types.Count;i++){
				long outs = tokenoutput_amounts[token_types[i]];
				long ins = tokeninput_amounts[token_types[i]];
				if(tokenoutput_amounts[token_types[i]] != tokeninput_amounts[token_types[i]]){
					//We are not spending the right amount of token inputs. We must spend the entire input
					BlockHelperLog("Token spent must equal token input. Entire balance must be spent");
					return null;
				}
			}
			
			//Create the array of unused TXouts that we will use for this NTP1 transaction
			JArray txins = new JArray();
			foreach (JObject utxo in ordered_utxos) {
				JObject txin = new JObject();
				txin["txid"] = utxo["txid"];
				txin["vout"] = utxo["index"];
				txin["value"] = utxo["value"];
				txins.Add(txin);
			}
			
			List<NTP1Instructions> TiList = new List<NTP1Instructions>();
			JArray txouts = new JArray();
			JObject txout;
			int index = 0;
			foreach (JObject target in ordered_targets) {
				txout = new JObject();
				txout["index"] = index;
				txout["amount"] = target["amount"];
				txout["tokenId"] = target["tokenId"];
				txout["address"] = target["address"];
				txouts.Add(txout);
				
				//Now make the transfer instruction
				NTP1Instructions ti = new NTP1Instructions();
				ti.amount = Convert.ToUInt64(txout["amount"].ToString());
				ti.vout_num = index;
				TiList.Add(ti);
				index++;
			}
			
			//Create the hex op_return
			string ti_script = _NTP1CreateTransferScript(TiList,null); //No metadata
			
			//The last txout is the op_return
			txout = new JObject();
			txout["index"] = index;
			txout["script"] = ti_script;
			txouts.Add(txout);
			
			JObject result = new JObject();
			result["usedutxos"] = txins;
			result["vouts"] = txouts;
			
			return result;
		}
		
		public static string CreateNTP1Transaction(string from_add,JArray targets)
		{
			//The format received of the targets is [{address="",amount="",tokenid=""}]
			//Neblio RPC expects format {address={tokenid=amount}},{address={tokenid=amount}}
			//This function is not effective if the wallet doesn't have the desired token
			//Until Neblio provides option to skip tokenid validation
			
			List<string> token_types = new List<string>();
			foreach (JObject target in targets) {
				string unique_id = target["tokenId"].ToString();
				bool exist = false;
				for(int i = 0;i < token_types.Count;i++){
					if(unique_id == token_types[i]){
						exist = true;break; //We already have this token type in our list
					}
				}
				if(exist == false){
					//Add this new token type to our list
					token_types.Add(unique_id);
				}
			}
			
			if(token_types.Count < 1){return "";} //Nothing to send
		
			//Now we have a list of unique tokens, we must follow the order for this to work correctly
			JArray unordered_utxos = GetUnspentUTXOJSONFromDatabase(from_add);
			if(unordered_utxos == null){return "";} //There are no unspent utxos
			
			JArray ordered_utxos = new JArray();
			JArray ordered_targets = new JArray();
			for(int i = 0;i < token_types.Count;i++){
				bool match = false;
				foreach (JObject utxo in unordered_utxos) {
					foreach (JObject token in utxo["tokens"]) {
						//Go through the list of tokens in this UTXO, only look at the first token
						if(token["tokenId"].ToString() == token_types[i]){
							match = true;
							ordered_utxos.Add(utxo);
						}
						break;
					}
				}
				if(match == false){
					return ""; //This means for this token type, it couldn't find a corresponding UTXO
				}
				foreach (JObject target in targets) {
					if(target["tokenId"].ToString() == token_types[i]){
						ordered_targets.Add(target);
					}
				}
				//By the end of this, we should have an ordered list of utxos based on order of token types sorted by first tokenid
				//We should also have an ordered list of targets sorted by tokenid
			}
			
			if(ordered_utxos.Count < 1){return "";} //There are no matching UTXOs for the token type
			
			//Now prepare the RPC call
			JArray txins = new JArray();
			foreach (JObject utxo in ordered_utxos) {
				JObject txin = new JObject();
				txin["txid"] = utxo["txid"];
				txin["vout"] = utxo["index"];
				txins.Add(txin);
			}
			
			//We are going to preformat our JSON to allow for duplicated keys (something that JSON.Net doesn't do normally allow)
			StringBuilder to_container_build = new StringBuilder();
			JsonTextWriter to_write = new JsonTextWriter(new StringWriter(to_container_build));
			to_write.WriteStartObject();
			foreach (JObject target in ordered_targets) {
				string address = target["address"].ToString();
				string id = target["tokenId"].ToString();
				ulong amount = Convert.ToUInt64(target["amount"].ToString());
				to_write.WritePropertyName(address);
				to_write.WriteStartObject();
				to_write.WritePropertyName(id);
				to_write.WriteValue(amount);
				to_write.WriteEndObject();
			}
			to_write.WriteEndObject();
			to_write.Close();
			string to_container = to_container_build.ToString();
			
			//Now perform the RPC call
			string tx_hex = CreateRPCNTP1Transaction(txins,to_container);
			return tx_hex;
		}
		
		public static void RollbackDatabase(int numblocks)
		{
			//This method rollbacks the database by the number of blocks
			BlockHelperLog("Attempting to roll back database by "+numblocks+" blocks");
			SQLiteConnection mycon = ConnectDB();
			for(int i = 0;i < numblocks;i++){
				
				//First get the last block in the database
				string myquery = "Select blocknum, blockhash From BLOCK Order By nindex DESC Limit 1";
				SQLiteCommand statement = new SQLiteCommand(myquery,mycon);
				SQLiteDataReader statement_reader = statement.ExecuteReader();			
				bool dataavail = statement_reader.Read();
				if(dataavail == false){
					//Not enough data remaining for this rollback
					throw new Exception("Failed to complete rollback. Not enough data available");
				}
				Block bk = new Block();
				bk.blocknum = Convert.ToInt64(statement_reader["blocknum"].ToString());
				bk.blockhash = statement_reader["blockhash"].ToString();
				statement_reader.Dispose();
				statement.Dispose();
				BlockHelperLog("Rolling back block: "+bk.blocknum+" ("+bk.blockhash+")");
				
				//Then get the transasctions in the block
				myquery = "Select transactionhash, encoded_string From BLOCKTRANSACTION Where blockhash = @bhash";
				statement = new SQLiteCommand(myquery,mycon);
				statement.Parameters.AddWithValue("@bhash",bk.blockhash);
				statement_reader = statement.ExecuteReader();
				while(statement_reader.Read()){
					BlockTransaction tx = new BlockTransaction();
					tx.blockhash = bk.blockhash;
					tx.blocknum = bk.blocknum;
					tx.transactionhash = statement_reader["transactionhash"].ToString();
					string json = statement_reader["encoded_string"].ToString();
					//Decode to get all the VINs, we will reactivate those UTXOs
					JObject js = JObject.Parse(json);
					foreach (JToken vin in js["vin"]) {
						if(vin["coinbase"] != null){
							continue; //Skip this vin if it is a coinbase vin
						}
						UTXO txvin = new UTXO();
						txvin.spending = true;
						txvin.transactionhash = vin["txid"].ToString();
						txvin.vout_index = Convert.ToInt32(vin["vout"].ToString());
						tx.utxo_list.Add(txvin);
					}
					bk.blocktx_list.Add(tx);
				}
				statement_reader.Dispose();
				statement.Dispose();
				
				//Now create a SQLite transaction that will:
				//Remove the UTXOs from the transactions to be deleted
				//Then unspend the UTXOs that these transactions use
				//Then delete the transactions in the block
				//Then delete the block
				myquery = "BEGIN TRANSACTION";
				statement = new SQLiteCommand(myquery,mycon); //Create a transaction to make inserts faster
				statement.ExecuteNonQuery();
				statement.Dispose();
				
				myquery = "Delete From UTXO Where transactionhash = @hash";
				statement = new SQLiteCommand(myquery,mycon);
				for(int i2 = 0;i2 < bk.blocktx_list.Count;i2++){
					//Go through each transaction and delete their UTXOs
					BlockTransaction tx = bk.blocktx_list[i2];
					statement.Parameters.AddWithValue("@hash",tx.transactionhash);
					statement.ExecuteNonQuery();
					statement.Parameters.Clear();
				}
				statement.Dispose();
				
				//Now update the previously spent UTXO and mark them as unspent and remove their spendsig
				myquery = "Update UTXO Set spent = 0, spendsig = '' Where transactionhash = @hash And vout_index = @n;";
				statement = new SQLiteCommand(myquery,mycon);				
				for(int i2 = 0;i2 < bk.blocktx_list.Count;i2++){
					//Go through each transaction and change the spent UTXO back to unspent
					BlockTransaction tx = bk.blocktx_list[i2];
					for(int i3=0;i3 < tx.utxo_list.Count;i3++){
						UTXO xo = tx.utxo_list[i3];
						if(xo.spending == false){continue;}
						statement.Parameters.AddWithValue("@hash",xo.transactionhash);
						statement.Parameters.AddWithValue("@n",xo.vout_index);
						statement.ExecuteNonQuery();
						statement.Parameters.Clear();						
					}
				}
				
				//Now delete all the transactions in this block
				myquery = "Delete From BLOCKTRANSACTION Where blockhash = @bhash";
				statement = new SQLiteCommand(myquery,mycon);
				statement.Parameters.AddWithValue("@bhash",bk.blockhash);
				statement.ExecuteNonQuery();
				statement.Parameters.Clear();
				
				//Now finally delete the block
				myquery = "Delete From BLOCK Where blockhash = @bhash";
				statement = new SQLiteCommand(myquery,mycon);
				statement.Parameters.AddWithValue("@bhash",bk.blockhash);
				statement.ExecuteNonQuery();
				statement.Parameters.Clear();
				
				myquery = "COMMIT TRANSACTION";
				statement = new SQLiteCommand(myquery,mycon); //Create a transaction to make inserts faster
				statement.ExecuteNonQuery();
				statement.Dispose();
			}
			mycon.Dispose();
		}
		
		public static bool AddBlockToDatabase(Block bk,SQLiteConnection mycon)
		{
			//Returns true if successfully added the block to the database with fully processed = 1
			//Block, Transactions and UTXO insertions and changes are all in one big transaction
			//Theoretically, if all the data is not written at once, it will rollback
			if(bk.blockhash.Length == 0){return false;}
			bool local_connection = false;
			if(mycon == null){
				local_connection = true;
			}
			try{
				string myquery;
				SQLiteCommand statement;
				if(local_connection == true){
					mycon = ConnectDB();					
					myquery = "BEGIN TRANSACTION";
					statement = new SQLiteCommand(myquery,mycon); //Create a transaction to make inserts faster
					statement.ExecuteNonQuery();
					statement.Dispose();
				}
	
				//Insert the block
				myquery = "Insert Into BLOCK (blocknum,blockhash,blocktime) Values (@num,@hash,@time);";
				statement = new SQLiteCommand(myquery,mycon);
				statement.Parameters.AddWithValue("@num",bk.blocknum);
				statement.Parameters.AddWithValue("@hash",bk.blockhash);
				statement.Parameters.AddWithValue("@time",bk.blocktime);
				statement.Parameters.AddWithValue("@full",0); //Not fully processed yet
				statement.ExecuteNonQuery();
				statement.Dispose();
	
				//Insert the transactions now and not any UTXOs
				myquery = "Insert Into BLOCKTRANSACTION (blocknum,blockhash,transactionhash,encoded_string,vin_count,vout_count) Values (@num,@bhash,@thash,@json,@vin,@vout);";
				statement = new SQLiteCommand(myquery,mycon);
				for(int i = 0;i < bk.blocktx_list.Count;i++){
					BlockTransaction tx = bk.blocktx_list[i];
					statement.Parameters.AddWithValue("@num",tx.blocknum);
					statement.Parameters.AddWithValue("@bhash",tx.blockhash);
					statement.Parameters.AddWithValue("@thash",tx.transactionhash);
					statement.Parameters.AddWithValue("@json",tx.encoded_string);
					statement.Parameters.AddWithValue("@vin",tx.vin_count);
					statement.Parameters.AddWithValue("@vout",tx.vout_count);
					statement.ExecuteNonQuery();
					statement.Parameters.Clear(); //Remove the parameters we added
				}
				statement.Dispose();
				
				//Insert the new UTXOs into the database
				myquery = "Insert Into UTXO (transactionhash,vout_index,neb_address,encoded_string,spent,spendsig) Values (@hash,@n,@add,@json,0,'');";
				statement = new SQLiteCommand(myquery,mycon);
				for(int i = 0;i < bk.blocktx_list.Count;i++){
					BlockTransaction tx = bk.blocktx_list[i];
					//Now focus specifically on the UTXOs this transaction has created
					for(int i2 = 0;i2 < tx.utxo_list.Count;i2++){
						UTXO xo = tx.utxo_list[i2];
						if(xo.spending == true){continue;} //Skip outs that we are spending
						statement.Parameters.AddWithValue("@hash",xo.transactionhash);
						statement.Parameters.AddWithValue("@n",xo.vout_index);
						statement.Parameters.AddWithValue("@add",xo.neb_address);
						statement.Parameters.AddWithValue("@json",xo.encoded_string);
						statement.ExecuteNonQuery();
						statement.Parameters.Clear(); //Remove the parameters we added					
					}
				}
				statement.Dispose();

				//Now update the spent UTXO and mark them as inactive/spent plus add a spentsig
				myquery = "Update UTXO Set spent = 1, spendsig = @sig Where transactionhash = @hash And vout_index = @n;";
				statement = new SQLiteCommand(myquery,mycon);
				for(int i = 0;i < bk.blocktx_list.Count;i++){
					BlockTransaction tx = bk.blocktx_list[i];
					//Now focus specifically on the UTXOs this transaction has used
					for(int i2 = 0;i2 < tx.utxo_list.Count;i2++){
						UTXO xo = tx.utxo_list[i2];
						if(xo.spending == false){continue;} //Skip outs that we are creating
						statement.Parameters.AddWithValue("@hash",xo.transactionhash);
						statement.Parameters.AddWithValue("@n",xo.vout_index);
						statement.Parameters.AddWithValue("@sig",xo.spendsig);
						statement.ExecuteNonQuery();
						statement.Parameters.Clear(); //Remove the parameters we added					
					}
				}
				statement.Dispose();
				
				if(local_connection == true){
					myquery = "COMMIT TRANSACTION";
					statement = new SQLiteCommand(myquery,mycon); //Create a transaction to make inserts faster
					statement.ExecuteNonQuery();
					statement.Dispose();
				}
				return true;
			}catch(Exception e){
				BlockHelperLog("Failed to add block "+bk.blocknum+" to database, error: "+e.ToString());
			}finally{
				if(mycon != null && local_connection == true){
					mycon.Dispose();
				}
			}
			return false;
		}
		
		//Global exception handler, write to file
		public static void SetupExceptionHandlers()
		{
			#if !DEBUG
				BlockHelperLog("Loading release mode exception handlers");
		        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
		            LastExceptionHandler((Exception)e.ExceptionObject, "AppDomain.CurrentDomain.UnhandledException");
		
		        TaskScheduler.UnobservedTaskException += (s, e) =>
		            LastExceptionHandler(e.Exception, "TaskScheduler.UnobservedTaskException");
	        #endif
		}
		
		public static void LastExceptionHandler(Exception e,string source)
		{
			//Only for release mode, otherwise we want it to crash
			BlockHelperLog("A fatal unhandled error has occurred");
			if(e != null){
				BlockHelperLog(source+": "+e.ToString()); //Write the exception to file
			}
			if(lockfile != null){
				lockfile.Close();
				lockfile = null;
			}
			Console.WriteLine("This program encountered a fatal error and will close");
			Close_Program();
		}
		
		public static void Close_Program()
		{
			SetServer(false);
			Console.WriteLine("Press any key to exit the program...");
			Console.ReadKey();
			Environment.Exit(0);			
		}
		
		public static System.Object debugfileLock = new System.Object(); 
		public static void BlockHelperLog(string msg)
		{
			System.Diagnostics.Debug.WriteLine(msg);
			//Also write this information to a log
			lock(debugfileLock){
				try {
					using (StreamWriter file = File.AppendText(App_Path+"/blockdata/debug.log"))
					{
						string format_time = UTC2DateTime(UTCTime()).ToString("MM-dd:HH-mm-ss");
					  	file.WriteLine(format_time+": "+msg);
					}			
				} catch (Exception) { }
			}
		}
		
		//Converts UTC seconds to DateTime object
		private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0,
		                                                          DateTimeKind.Utc);
		public static DateTime UTC2DateTime(int utc_seconds)
		{
		    return UnixEpoch.AddSeconds(utc_seconds);
		}
		
		public static int UTCTime()
		{
			//Returns time since epoch
			TimeSpan t = DateTime.UtcNow - UnixEpoch;
			return (int)t.TotalSeconds;
		}
		
		public static void ConsoleClearLine()
		{
			Console.SetCursorPosition(0,Console.CursorTop);
			Console.Write(new string(' ',Console.BufferWidth));
			Console.SetCursorPosition(0,Console.CursorTop-1);
		}
	}
}