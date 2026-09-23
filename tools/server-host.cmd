@echo off
rem The SPT server as a MAP PICTURE HOST, in a visible console. Double-click it.
rem QUESTTREE_ACCEPT_MAPS=1 is all it sets: without it the server refuses every map picture a client
rem offers, and with it a capture lands in user\mods\QuestTree\maps\ - the only source -RefreshMaps has.
rem From 1.19.0 the same variable admits a capture's 3D MESH (<key>-mesh.bin) beside its pictures: one
rem opt-in for both, because a mesh is the same thing - a peer's bytes that every other client then
rem looks at. A set whose capture built a mesh is not served until both have arrived.
set QUESTTREE_ACCEPT_MAPS=1
cd /d C:\Games\SPT\SPT_Runtime
echo Quest Tracker map picture uploads ACCEPTED. Close this window to stop the server.
"C:\Games\SPT\SPT_Runtime\SPT.Server.exe"
