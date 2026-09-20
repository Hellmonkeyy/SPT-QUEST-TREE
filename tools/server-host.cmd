@echo off
rem The SPT server as a MAP PICTURE HOST, in a visible console. Double-click it.
rem QUESTTREE_ACCEPT_MAPS=1 is all it sets: without it the server refuses every map picture a client
rem offers, and with it a capture lands in user\mods\QuestTree\maps\ - the only source -RefreshMaps has.
set QUESTTREE_ACCEPT_MAPS=1
cd /d C:\Games\SPT\SPT_Runtime
echo Quest Tracker map picture uploads ACCEPTED. Close this window to stop the server.
"C:\Games\SPT\SPT_Runtime\SPT.Server.exe"
