set -u
DLL=dotnet/src/Starcloud.Bridge/bin/Debug/net8.0/Starcloud.Bridge.dll
export STARCLOUD_DATA_DIR="$PWD/.scratch/data"
printf '%s\n' \
 '{"id":1,"method":"ping","params":{}}' \
 '{"id":2,"method":"bridge.methods","params":{}}' \
 '{"id":3,"method":"settings.get","params":{}}' \
 '{"id":4,"method":"accounts.list","params":{}}' \
 '{"id":5,"method":"catalog.versions","params":{}}' \
 '{"id":6,"method":"catalog.installed","params":{}}' \
 '{"id":7,"method":"version.settings","params":{"versionId":"1.20.1"}}' \
 '{"id":8,"method":"version.detail","params":{"versionId":"1.20.1"}}' \
 '{"id":9,"method":"versions.backupList","params":{}}' \
 '{"id":10,"method":"java.list","params":{}}' \
 '{"id":11,"method":"runtime.state","params":{}}' \
 '{"id":12,"method":"multiplayer.state","params":{}}' \
 '{"id":13,"method":"plugins.list","params":{}}' \
 '{"id":14,"method":"skins.list","params":{}}' \
 '{"id":15,"method":"saves.list","params":{}}' \
 '{"id":16,"method":"nova.config.get","params":{}}' \
 '{"id":17,"method":"lab.palette","params":{}}' \
 '{"id":18,"method":"starcloud.state","params":{}}' \
 '{"id":19,"method":"not.exist","params":{}}' \
 '{"id":20,"method":"lab.assets","params":{}}' \
 '{"id":21,"method":"skins.bs6.status","params":{}}' \
 'not json at all' \
 '{"id":23,"method":"ping","params":{}}' \
 | dotnet "$DLL"
