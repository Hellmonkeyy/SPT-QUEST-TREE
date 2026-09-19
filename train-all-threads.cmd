@echo off
rem Weapon-build TRAINING on every thread, in a visible console. Double-click it.
rem Same as train.ps1 with QUESTTREE_TRAIN_THREADS set to the machine's thread count, so the
rem search uses all of them instead of the default half. Stops on its own after 340,000 attempts
rem or 5 hours; close the window sooner if you like - every improvement is written as it is found.
rem Must run from the server's own folder: SPT.Server.exe reads .\sptLogger.json from there.
set QUESTTREE_TRAIN=1
set QUESTTREE_TRAIN_THREADS=%NUMBER_OF_PROCESSORS%
cd /d C:\Games\SPT\SPT_Runtime
echo Training on %NUMBER_OF_PROCESSORS% threads. Close this window to stop.
SPT.Server.exe
