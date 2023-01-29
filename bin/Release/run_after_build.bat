ILMerge.exe PoolWatcher.exe CommandLine.dll /v4 /out:PoolWatcherPacked.exe

move /Y PoolWatcherPacked.exe PoolWatcher.exe
del /F /Q PoolWatcherPacked.pdb
del /F /Q PoolWatcher.pdb
del /F /Q PoolWatcher.exe.config
del /F /Q CommandLine.dll