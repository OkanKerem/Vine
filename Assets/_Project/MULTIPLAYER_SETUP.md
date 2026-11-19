# Multiplayer foundation (Mirror 96, KCP, listen server)

## One-time setup
1. Open the project in Unity 6000.3. Wait for scripts to compile (Console must be free of errors).
2. Menu **Tools > Vineyard > Build Multiplayer Test Scene**.
   This generates:
   - `Assets/_Project/Prefabs/NetworkPlayer.prefab`
   - `Assets/_Project/Materials/*.mat`
   - `Assets/_Project/Scenes/MultiplayerTest.unity` (added as build index 0)
3. Open `MultiplayerTest.unity` (the builder leaves it open).

Re-running the menu item regenerates (overwrites) these assets.

## Scripts
| Script | Role |
|---|---|
| `Scripts/Networking/GameNetworkManager.cs` | NetworkManager subclass, max 4 players, disconnect helper, last error |
| `Scripts/Player/NetworkPlayer.cs` | Input, look, prediction/reconciliation, host simulation, remote interpolation |
| `Scripts/Player/PlayerMotor.cs` | CharacterController movement step (walk, sprint, jump, gravity) |
| `Scripts/Player/PlayerInputCmd.cs` | Per-tick input struct sent to the host |
| `Scripts/UI/ConnectionUI.cs` | IMGUI panel: IP, port, Host, Join, Disconnect |
| `Editor/MultiplayerSceneBuilder.cs` | Builds the prefab and test scene |

Gameplay scripts use only Mirror abstractions. `kcp2k.KcpTransport` is referenced only by the editor builder.
To switch transports, replace the transport component on the NetworkManager object and assign it to the **Transport** field.

## Controls
WASD move · Mouse look · Space jump · Left Shift sprint · Esc release cursor · Left click recapture cursor.

## Test on one PC
- Make a build (**File > Build Profiles > Build**), run it, and click **Host Game**.
- In the Editor, press Play, keep the IP as `localhost`, and click **Join Game**.
- You can also swap which side hosts, or run two builds side by side.

## Test on two PCs (LAN)
1. Both PCs need the same build.
2. Host PC: find its LAN IPv4 with `ipconfig` (for example `192.168.1.23`) and click **Host Game**.
3. Allow the app through Windows Firewall when asked, at least on Private networks. KCP uses **UDP 7777** by default.
4. Client PC: enter the host IP and click **Join Game**.
5. If the client cannot connect, check that both PCs are on the same subnet, the network profile is Private, and UDP 7777 is not blocked.

## Tunables (NetworkPlayer / PlayerMotor inspector)
`walkSpeed`, `sprintSpeed`, `jumpHeight`, `gravity`, `mouseSensitivity`, `stateSendEveryTicks`, `interpolationDelay`, `reconcileThreshold`, `maxInputsPerTick`.
