# NebliDex BlockHelper Guide
NebliDex BlockHelper is a program designed to index the Neblio blockchain through its interaction with Nebliod or Neblio-Qt. Once indexed, the BlockHelper runs its own RPC (Remote Procedure Call) server that applications can use to obtain Neblio blockchain related transaction and address information. BlockHelper will continue to sync with a synced Nebliod/Neblio-Qt. BlockHelper is designed to be used with NebliDex Critical Nodes but can be used with other applications simultaneously. BlockHelper will only connect to applications on the same computer.

## Getting Started
First grab the latest version of Nebliod or Neblio-Qt from the Neblio alpha builds repository: https://neblio-build-staging.ams3.digitaloceanspaces.com/index.html

If using Neblio-Qt, you must activate the RPC server (not on by default) by setting up a neblio.conf file in your Neblio data folder with an rpcuser, an rpcpass, an rpcport of 6326 (which is default) and server=1.

Next, wait for Nebliod/Neblio-Qt to completely sync to the Blockchain before running BlockHelper otherwise BlockHelper will tell you to wait till Nebliod is fully synced. Once Nebliod is synced, start BlockHelper and it will begin to index the Blockchain. The indexing process should be quicker than Nebliod indexing but it will still take several hours to complete. The blockchain index file will be a few gigabytes in size (at height 660,000) and will be stored in the blockdata subfolder.

Once BlockHelper is synced, if using with a NebliDex CN, when you Activate as a CN, it will automatically
detect the BlockHelper and show "BlockHelper Active" in the status bar of the NebliDex client if connected.

## Building NebliDex BlockHelper
NebliDex BlockHelper is built in C# using managed code from the .NET Library on Windows and Mono Framework on Mac and Linux.
BlockHelper uses Newtonsoft.JSON library (JSON.NET) and SQLite Library Version 3
### Mac
* Download Visual Studio for Mac
* Install Mono Framework (if not already included)
* Open Solution
* Build and Run in Terminal

If you want to create a bundle using mkbundle (see the build script inside the bin folder from the Mac repository)

### Linux
Depending on the exact distribution of Linux you are running the steps can vary.
* Install Mono Develop from here: https://www.monodevelop.com/download/#fndtn-download-lin
* Run code: `sudo apt-get install monodevelop`
* Open Solution
* Build and Run in Terminal

### Windows
* Make sure at least .NET Framework 4.5 is installed on your system
* Find your favorite C# code editor (Visual Studio, SharpDevelop,MonoDevelop)
* Open Solution
* Build and Run in Terminal

## Querying BlockHelper
See readme.html for information on how to query the BlockHelper

## Release Notes
### 1.0.0
This is the initial release of the NebliDex BlockHelper
