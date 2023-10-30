# PoolWatcher

It program for the mining pools monitoring and mining support.

# Parameters:
-k : Kill a pill before starting mining; variants: 0 or 1, default 0

-w : Run without windows for external miners; variants: 0 or 1, default 1

-s : Anti-watchdog support; if you use a bat-file or lnk-shortcut, set his name as well as the name of the executable file; variants: 0 or 1, default 1

-o : Direct procedure for completing the mining process; variants: 0 or 1, default 1

-e : Quit internal cycle after miner crash; variants: 0 or 1, default 1

-d : Using a dummy-miner (named as dummy.exe), by default it starts with parameters '--algo vds --server vds.666pool.cn --port 9338 --user VcXGox4tgyfGP1qkPcrCqzxpGSvpEZzhP1X@pps.FARM --pass x --pec 0 --watchdog 0', unless otherwise specified in the fifth line of the configuration file; variants: 0 or 1, default 0

-p : Base waiting period for the launch of the miner, default 360 seconds

-q : Base waiting period for the first share, default 1200 seconds

-i : Ignore messages like 'no active pools'; variants: 0 or 1, default 1

-v : Ban-time for the pool (minutes), default 30 minutes

-m : Behavior upon occurrence of an event "For a long time there is no shares"; variants: 0 (ban) or 1 (miner restart), by default 1

# Additional info:
Example: PoolWatcher.exe -k 0 -w 1 -s 1 -o 1 -e 1 -d -p 360 -q 1200 -i 1 -v 30 -m 1

Warning: if you use SRBMiner-Multi, the pauses between portions of messages can reach 2 or more minutes, the base period of waiting for the miner should be larger, otherwise conflicts will arise in the processing of information

When starting batch-files, the monitoring of the following processes is turned off: "OhGodAnETHlargementPill-r2.exe", "sleep", "timeout""MSIAfterburner.exe", "curl", "tasklist", "find", "powershell", "start", "cd" and "taskkill"

To complete mining, use the closure of the main window with a cross, call "Ctrl+C" or function "End the process tree" in "Task Manager"!

# System requirements:
Windows 7-11 with .NET Framework 4.5 and above

# Donate wallets:
BTC     -----     bc1q2dpwy93qmmnq4utuu8ypmj3kqykm43p4wg5gpt
