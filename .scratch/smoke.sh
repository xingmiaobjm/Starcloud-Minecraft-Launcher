#!/bin/bash
DLL="$1"
send() { printf '%s\n' "$@"; }
send \
 '{"id":1,"method":"ping","params":{}}' \
 '{"id":2,"method":"app.paths","params":{}}' \
 '{"id":3,"method":"settings.get","params":{}}' \
 '{"id":4,"method":"accounts.list","params":{}}' \
 '{"id":5,"method":"saves.list","params":{}}' \
 '{"id":6,"method":"skins.list","params":{}}' \
 '{"id":7,"method":"nova.config.get","params":{}}' \
 '{"id":8,"method":"lab.palette","params":{}}' \
 '{"id":9,"method":"runtime.state","params":{}}' \
 '{"id":10,"method":"runtime.gpu","params":{}}' \
 '{"id":11,"method":"multiplayer.state","params":{}}' \
 '{"id":12,"method":"plugins.list","params":{}}' \
 '{"id":13,"method":"catalog.versions","params":{}}' \
 '{"id":14,"method":"java.list","params":{}}' \
 '{"id":15,"method":"version.settings","params":{"versionId":"1.20.1"}}' \
 '{"id":16,"method":"versions.backupList","params":{}}' \
 '{"id":17,"method":"starcloud.state","params":{}}' \
 '{"id":18,"method":"settings.clearCache","params":{}}' \
 '{"id":19,"method":"not.exist","params":{}}' \
 '{"id":20,"method":"lab.assets","params":{}}' \
 '{"id":21,"method":"nova.plan.discard","params":{}}' \
 '{"id":22,"method":"skins.bs6.status","params":{}}' \
 'not json at all' \
 '{"id":23,"method":"ping","params":{}}' \
 | timeout 120 dotnet "$DLL" 2>&1
