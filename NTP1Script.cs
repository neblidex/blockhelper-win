﻿/*
 * Created by SharpDevelop.
 * User: David
 * Date: 2/20/2019
 * Time: 6:14 AM
 * 
 * To change this template use Tools | Options | Coding | Edit Standard Headers.
 */
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Collections;
using System.IO;

namespace BlockHelper
{
	/// <summary>
	/// Description of NTP1Script.
	/// </summary>
	public partial class Program
	{
		public class NTP1Transactions
		{
			public string raw_tx_string = "";
			public string ntp1_opreturn = "";
			public List<NTP1Instructions> ntp1_instruct_list;
			public NTP1Transactions(){
				ntp1_instruct_list = new List<NTP1Instructions>();
			}
			public int tx_type = 0;//0 = Issue, 1 = Transfer, 2 = Burn
			public byte[] metadata = null;
		}
		
		public class NTP1Instructions
		{
			//Transfer instructions do not have a tokenid, they transfer based on the token position in the vins
			public ulong amount = 0; //Amount of token
			public int vout_num = 0; //Which vout
			public byte firstbyte = 0;
			public bool skipinput = false;
		}
		
		//Transfer instructions will deplete a specific token (based on order of token in Input) to zero before next instructions
		//being applied to next token (based on order)
		//Metadata follows transfer instructions
		
		public static string _NTP1CreateTransferScript(List<NTP1Instructions> TIs,byte[] metadata)
		{
			if(TIs.Count == 0){return "";}
			if(TIs.Count > 255){return "";} //Cannot create transaction greater than 255 instructions
			
			//Constants
			byte[] header = ConvertHexStringToByteArray("4e5403"); //Represents chars NT and byte protocal version (3)
			byte op_code = 16; //Transer transaction
			int op_return_max_size = 4096; //Maximum size of the scriptbin
			
			using(MemoryStream scriptbin = new MemoryStream())
			{
				//This stream will hold the byte array that will be converted to a hex string eventually
				scriptbin.Write(header,0,header.Length); //Write the header data to the stream
				scriptbin.WriteByte(op_code);
				scriptbin.WriteByte(Convert.ToByte(TIs.Count)); //The amount of TIs
				
				for(int i = 0;i < TIs.Count;i++){
					//Add the transfer instructions
					NTP1Instructions ti = TIs[i];
					//Skip input will always be false in our case, so first position is always 0
					//The maximum vout position is 31 (0-31) per this method, although 255 TIs are supported
					if(ti.vout_num > 31){return "";}
					string output_binary = "000" + Convert.ToString(ti.vout_num,2).PadLeft(5,'0'); //Convert number to binary
					byte first_byte = Convert.ToByte(output_binary,2);
					scriptbin.WriteByte(first_byte);
					
					//Now convert the amount to byte array
					byte[] amount_bytes = _NTP1NumToByteArray(ti.amount);
					scriptbin.Write(amount_bytes,0,amount_bytes.Length);
				}
				
				//Add metadata if present
				if(metadata != null){
					uint msize = (uint)metadata.Length;
					if(msize > 0){
						byte[] msize_bytes = BitConverter.GetBytes(msize);
						if(BitConverter.IsLittleEndian == true){
							//We must convert this to big endian as protocol requires big endian
							Array.Reverse(msize_bytes);
						}
						scriptbin.Write(msize_bytes,0,msize_bytes.Length); //Write the size of the metadata
						scriptbin.Write(metadata,0,metadata.Length); //Write the length of the metadata
					}
				}
				if(scriptbin.Length > op_return_max_size){
					return ""; //Cannot create a script larger than the max
				}
				return ConvertByteArrayToHexString(scriptbin.ToArray());
			}
		}
		
		public static void _NTP1ParseScript(NTP1Transactions tx)
		{
			if(tx.ntp1_opreturn.Length == 0){return;} //Nothing inside
			byte[] scriptbin = ConvertHexStringToByteArray(tx.ntp1_opreturn);
			int protocol_version = Convert.ToInt32(scriptbin[2]);
			if(protocol_version != 3){
				throw new Exception("NTP1 Protocol Version less than 3 not currently supported");
			}
			int op_code = Convert.ToInt32(scriptbin[3]);
			int script_type = 0;
			if(op_code == 1){
				script_type = 0; //Issue transaction
			}else if(op_code == 16){
				script_type = 1; //Transfer transaction
			}else if(op_code == 32){
				script_type = 2; //Burn transaction
			}
			if(script_type != 1){
				throw new Exception("Can only parse NTP1 transfer scripts at this time");
			}
			tx.tx_type = 1;
			scriptbin = ByteArrayErase(scriptbin,4); //Erase the first 4 bytes
			
			//Now obtain the size of the transfer instructions
			int numTI = Convert.ToInt32(scriptbin[0]); //Byte number between 0 and 255 inclusively
			int raw_size = 1;
			scriptbin = ByteArrayErase(scriptbin,1);
			for(int i = 0;i < numTI;i++){
				NTP1Instructions ti = new NTP1Instructions();
				ti.firstbyte = scriptbin[0]; //This represents the flags
				int size = _CalculateAmountSize(scriptbin[1]); //This will indicate the size of the next byte sequence
				byte[] amount_byte = new byte[size];
				Array.Copy(scriptbin,1,amount_byte,0,size); //Put these bytes into the amount_byte array
				size++; //Now include the flag byte
				raw_size += size; //Our total instruction size added to the raw size
				scriptbin = ByteArrayErase(scriptbin,size); //Erase transfer instructions
				
				//Break down the first byte which represents the location and skipinput byte
				string firstbyte_bin = Convert.ToString(ti.firstbyte,2).PadLeft(8,'0'); //Byte to binary string
				string outputindex_bin = firstbyte_bin.Substring(3);
				if(firstbyte_bin[7] == '1'){
					ti.skipinput = true; //This means the byte value is 255
				}
				ti.vout_num = Convert.ToInt32(Convert.ToUInt64(outputindex_bin,2));
				int len = amount_byte.Length;
				ti.amount = _NTP1ByteArrayToNum(amount_byte);
				tx.ntp1_instruct_list.Add(ti);
			}
			
			//Now extract metadata from the op_return. Max op_return size: 4096 bytes
			if(scriptbin.Length < 4){
				return; //No metadata or not enough data remaining to extract
			}
			
			//Get the metadata size int32
			byte[] metadata_size_bytes = new byte[4];
			Array.Copy(scriptbin,metadata_size_bytes,4); //The size of the metadata is stored as Bigendian in the script
			//Determine if the system is little endian or not
			if(BitConverter.IsLittleEndian == true){
				Array.Reverse(metadata_size_bytes);
			}
			//Now put the bytes into an int
			uint metadata_size = BitConverter.ToUInt32(metadata_size_bytes,0);
			scriptbin = ByteArrayErase(scriptbin,4); //Remove the size int on the remaining data
			if(scriptbin.Length != metadata_size){
				throw new Exception("Metadata information is invalid");
			}
			
			tx.metadata = scriptbin; //Our metadata is what remains in the script. Just raw data to be used for any purpose
		}
		
		public static byte[] ConvertHexStringToByteArray(string hexString)
		{
		    if (hexString.Length % 2 != 0)
		    {
		        throw new ArgumentException(String.Format(CultureInfo.InvariantCulture, "The binary key cannot have an odd number of digits: {0}", hexString));
		    }
		
		    byte[] data = new byte[hexString.Length / 2];
		    for (int index = 0; index < data.Length; index++)
		    {
		        string byteValue = hexString.Substring(index * 2, 2);
		        data[index] = byte.Parse(byteValue, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
		    }
		
		    return data; 
		}
		
		public static byte[] ByteArrayErase(byte[] arr,long length)
		{
			//This will effectively delete a part of the byte array
			byte[] new_arr = new byte[arr.Length - length];
			Array.Copy(arr,length,new_arr,0,new_arr.Length);
			return new_arr;
		}
		
        public static int _CalculateAmountSize(byte val)
        {
			string binval = Convert.ToString(val,2).PadLeft(8,'0'); //Byte to binary string
			binval = binval.Substring(0,3);
			ulong newval = Convert.ToUInt64(binval,2);
			if (newval < 6) {
				return Convert.ToInt32(newval) + 1;
			} else {
				return 7;
			}
        }
        
		public static string ReverseString(string s)
		{
		    char[] arr = s.ToCharArray();
		    Array.Reverse(arr);
		    return new string(arr);
		}
		
		public static string ConvertByteArrayToHexString(byte[] arr)
		{
			string[] result = new string[arr.Length];
			for(int i = 0;i < arr.Length;i++){
				result[i] = arr[i].ToString("X2").ToLower(); //Hex format
			}
			return String.Concat(result); //Returns all the strings together	
		}
		
		public static byte[] _NTP1NumToByteArray(ulong amount)
		{
			//We need to calculate the Mantissa and Exponent for this number
			if(amount == 0){return null;}
			string amount_string = amount.ToString();
			//Get the amount of significant digits
			int zerocount = 0;
			for(int i = amount_string.Length-1;i >= 0;i--){
				if(amount_string[i] == '0'){
					zerocount++;
				}else{
					break;
				}
			}
			
			int significant_digits = amount_string.Length - zerocount;
			if(amount < 32 || significant_digits > 12){
				zerocount = 0; //If the significant digits are too large or amount is small, do not utilize exponent
				significant_digits = amount_string.Length;
			}
			
			long mantissa = Convert.ToInt64(amount_string.Substring(0,significant_digits));
			string mantissa_binary = Convert.ToString(mantissa,2);
			string exponent_binary = Convert.ToString(zerocount,2);
			if(zerocount == 0){
				//No exponent
				exponent_binary = "";
			}
			
			//No need to remove the leading zeros as Neblio does due to the fact that C# creates binary string without leading zeros
			int mantissaSize = 0;			
			int exponentSize = 0; 
			string header;
			
			if (mantissa_binary.Length <= 5 && exponent_binary.Length == 0) {
				header       = "000";
				mantissaSize = 5;
				exponentSize = 0;
			} else if (mantissa_binary.Length <= 9 && exponent_binary.Length <= 4) {
				header       = "001";
				mantissaSize = 9;
				exponentSize = 4;
			} else if (mantissa_binary.Length <= 17 && exponent_binary.Length <= 4) {
				header       = "010";
				mantissaSize = 17;
				exponentSize = 4;
			} else if (mantissa_binary.Length <= 25 && exponent_binary.Length <= 4) {
				header       = "011";
				mantissaSize = 25;
				exponentSize = 4;
			} else if (mantissa_binary.Length <= 34 && exponent_binary.Length <= 3) {
				header       = "100";
				mantissaSize = 34;
				exponentSize = 3;
			} else if (mantissa_binary.Length <= 42 && exponent_binary.Length <= 3) {
				header       = "101";
				mantissaSize = 42;
				exponentSize = 3;
			} else if (mantissa_binary.Length <= 54 && exponent_binary.Length == 0) {
				header       = "11";
				mantissaSize = 54;
				exponentSize = 0;
			} else {
				//Can't encode binary format
				return null;
			}
			
			//Pad the mantissa and exponent binary format
			mantissa_binary = mantissa_binary.PadLeft(mantissaSize,'0'); 
			exponent_binary = exponent_binary.PadLeft(exponentSize,'0');
			
			string combined_binary = header + mantissa_binary + exponent_binary;
			if(combined_binary.Length % 8 != 0){
				//Not divisible by 8, binary format incorrect
				return null;
			}
			
			byte[] encodedamount = new byte[combined_binary.Length / 8];
			for(int i = 0;i < combined_binary.Length;i+=8){
				//Go through the binary string and find the bytes and put it in encodedamount
				byte b = Convert.ToByte(combined_binary.Substring(i,8),2);
				encodedamount[i / 8] = b;
			}
			
			return encodedamount;
		}
        
        public static ulong _NTP1ByteArrayToNum(byte[] byteval)
        {
        	if(byteval.Length > 7){
        		throw new Exception("Too many bytes to read. Cannot process");
        	}
        	int length = byteval.Length;
        	string bin_set = "";
        	for(int i = 0;i < length;i++){
        		//Create a new binary set that represents this entire byte sequence but byte sequence in reverse order and bit sequence in reverse order
        		string binary_val = Convert.ToString(byteval[length-i-1],2).PadLeft(8,'0'); //Get the binary representation of this char value
        		binary_val = ReverseString(binary_val); //Reverse the binary number
        		bin_set += binary_val;
        	}

			int bit0 = Convert.ToInt32(char.GetNumericValue(bin_set[bin_set.Length - 1]));
			int bit1 = Convert.ToInt32(char.GetNumericValue(bin_set[bin_set.Length - 2]));
			int bit2 = Convert.ToInt32(char.GetNumericValue(bin_set[bin_set.Length - 3]));

			// sizes in bits			
			int headerSize   = 0;			
			int mantissaSize = 0;			
			int exponentSize = 0;
			
			if (bit0 > 0 && bit1 > 0) {				
				headerSize   = 2;				
				mantissaSize = 54;				
				exponentSize = 0;
			} else {			
				headerSize = 3;				
				if (bit0 == 0 && bit1 == 0 && bit2 == 0) {				
					mantissaSize = 5;					
					exponentSize = 0;				
				} else if (bit0 == 0 && bit1 == 0 && bit2 == 1) {				
					mantissaSize = 9;					
					exponentSize = 4;				
				} else if (bit0 == 0 && bit1 == 1 && bit2 == 0) {				
					mantissaSize = 17;					
					exponentSize = 4;				
				} else if (bit0 == 0 && bit1 == 1 && bit2 == 1) {				
					mantissaSize = 25;					
					exponentSize = 4;				
				} else if (bit0 == 1 && bit1 == 0 && bit2 == 0) {				
					mantissaSize = 34;					
					exponentSize = 3;				
				} else if (bit0 == 1 && bit1 == 0 && bit2 == 1) {				
					mantissaSize = 42;					
					exponentSize = 3;				
				} else {				
					throw new Exception("Binary format not accepted");
				}			
			}
			
			// ensure that the total size makes sense			
			int totalBitSize = headerSize + mantissaSize + exponentSize;
			if ((totalBitSize / 8) != byteval.Length || (totalBitSize % 8) != 0) {				
				throw new Exception("Value of binary digits not valid");
			}
			
			//Now reverse the bin_set back to original form before reading the mantissa and the exponent
			bin_set = ReverseString(bin_set);
			string mantissa  = bin_set.Substring(headerSize, mantissaSize);			
			string exponent  = bin_set.Substring(headerSize + mantissaSize, exponentSize);
			if(exponent.Length == 0){
				//Zero exponent
				exponent = "0000";
			}

			ulong mantissa_val = Convert.ToUInt64(mantissa,2);			
			ulong exponent_val = Convert.ToUInt64(exponent,2);

			ulong amount = Convert.ToUInt64(Convert.ToDecimal(mantissa_val) * Convert.ToDecimal(Math.Pow(10,exponent_val)));
        	return amount;
        }
	}
}
