# CW.Server

A backend server for the Contract Wars client written as an ASP.NET Core Web
API.

## Requirements

- .NET SDK 9.0 or later

## Build

```powershell
dotnet build -c Release
```

The build copies `CW.Server_Data` into the output next to the exe, so the result
is runnable as-is.

## Run

```powershell
.\bin\Release\net9.0\CW.Server.exe
```

The startup banner confirms where it loaded its data from:

```
cw-server listening on http://0.0.0.0:8099
  data root       : ...\bin\Release\net9.0\CW.Server_Data
  static datasets : 22 in backend_data/
```

Stop it with Ctrl+C. The exe is locked while running, so stop it before
rebuilding.

## Configuration

Edit `CW.Server_Data\server.json` **in the project**, then rebuild. The build
copies it to the output, so edits made under `bin\` are overwritten.

| Key | Default | Meaning |
| --- | --- | --- |
| `Host` | `0.0.0.0` | listen address. `127.0.0.1` for local only |
| `Port` | `8099` | must match what the client is pointed at |
| `PublicIp` | `0.0.0.0` | address advertised in the server browser when a host registers a LAN address. Set to your public IP if hosting for people outside your network |
| `FreshAccounts` | `true` | new accounts start at the real fresh-account state. `false` starts them fully unlocked |
| `UnlockAll` | `false` | serve all content unlocked without altering stored progression |

Any unknown email/password is registered on first login, so no account setup is
needed.

## Pointing the client at it

The client reaches the server through the `CW.ClientEmu` BepInEx plugin. In the
game folder, set `BepInEx\config\com.cw.clientemu.cfg`:

```ini
[General]
Host = 127.0.0.1:8099
```

Use the server machine's address instead of `127.0.0.1` if it is running
elsewhere.

## Data

`CW.Server_Data` lives next to the exe and holds everything the server owns:

| Folder | Contents |
| --- | --- |
| `backend_data\` | game datasets served to the client (weapons, skills, maps, prices) |
| `templates\` | profile and customization templates |
| `server_state\` | accounts, profiles, clans, transactions — created on first run |

`server_state` is player data. It is not in source control, and `dotnet clean` or
deleting `bin\` will destroy it — back it up before either.

## Notes

Only one instance may own the port. Windows lets a process bound to
`0.0.0.0:8099` coexist with one bound to `127.0.0.1:8099`, and which one answers
depends on the address the client used, so a forgotten instance can silently
serve different data. Check with:

```powershell
Get-NetTCPConnection -LocalPort 8099 -State Listen |
  Select LocalAddress, @{n='proc';e={(Get-Process -Id $_.OwningProcess).ProcessName}}
```
