@echo off
rem Weapon-build TRAINING on every thread, in a visible console. Double-click it.
rem Same as train.ps1 with QUESTTREE_TRAIN_THREADS set to the machine's thread count, so the
rem search uses all of them instead of the default half. Stops on its own after 340,000 attempts
rem or 5 hours; close the window sooner if you like - every improvement is written as it is found.
rem Runs from the server's own folder, because SPT.Server.exe reads .\sptLogger.json from there,
rem and calls the exe by full path, because cmd resolves a bare name against PATH, not the folder.
set QUESTTREE_TRAIN=1
set QUESTTREE_TRAIN_THREADS=%NUMBER_OF_PROCESSORS%
rem Map uploads accepted too: a capture raid run against a training server has to be able to hand its
rem pictures over, and a server launched from here is already a diagnostics server, not a player's.
set QUESTTREE_ACCEPT_MAPS=1
cd /d C:\Games\SPT\SPT_Runtime
echo Training on %NUMBER_OF_PROCESSORS% threads. Close this window to stop.
"C:\Games\SPT\SPT_Runtime\SPT.Server.exe"
