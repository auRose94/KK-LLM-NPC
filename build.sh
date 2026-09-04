#!/usr/bin/env bash
# Build KKLLMNPC.dll against BepInEx + UnityEngine + Photon + Assembly-CSharp.
# Source is split across src/*.cs (plugin LLMNPCPlugin + partial class NPCInstance).
# Usage: ./build.sh
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
KDIR="/mnt/gaming/SteamLibrary/steamapps/common/KoboldKare"
MGMT="$KDIR/KoboldKare_Data/Managed"
CORE="$KDIR/BepInEx/core"
cd "$SCRIPT_DIR"

mcs -target:library -out:KKLLMNPC.dll src/*.cs \
  -r:"$CORE/BepInEx.dll" \
  -r:"$CORE/0Harmony.dll" \
  -r:"$MGMT/UnityEngine.dll" \
  -r:"$MGMT/UnityEngine.CoreModule.dll" \
  -r:"$MGMT/UnityEngine.UI.dll" \
  -r:"$MGMT/UnityEngine.UIModule.dll" \
  -r:"$MGMT/UnityEngine.PhysicsModule.dll" \
  -r:"$MGMT/UnityEngine.AnimationModule.dll" \
  -r:"$MGMT/UnityEngine.TextRenderingModule.dll" \
  -r:"$MGMT/UnityEngine.UnityWebRequestModule.dll" \
  -r:"$MGMT/UnityEngine.AudioModule.dll" \
  -r:"$MGMT/UnityEngine.VideoModule.dll" \
  -r:"$MGMT/UnityEngine.ImageConversionModule.dll" \
  -r:"$MGMT/UnityEngine.AIModule.dll" \
  -r:"$MGMT/PhotonUnityNetworking.dll" \
  -r:"$MGMT/Photon3Unity3D.dll" \
  -r:"$MGMT/PhotonRealtime.dll" \
  -r:"$MGMT/Assembly-CSharp.dll" \
  -r:"$MGMT/Naelstrof.PenetrationTech.dll" \
  -r:"$MGMT/UnityEngine.IMGUIModule.dll" \
  -r:"$MGMT/KoboldKare.ISavable.dll"
cp KKLLMNPC.dll "$KDIR/BepInEx/plugins/KKLLMNPC.dll"
printf 'Built and deployed %s\n' "$KDIR/BepInEx/plugins/KKLLMNPC.dll"
