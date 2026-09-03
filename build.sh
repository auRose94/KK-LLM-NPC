#!/usr/bin/env bash
# Build KKLLMNPC*.dll against BepInEx + UnityEngine + Photon + Assembly-CSharp.
# Usage: ./build.sh          — builds instance 1 only
#        ./build.sh 3        — builds instances 1,2,3 into plugins/
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
KDIR="/mnt/gaming/SteamLibrary/steamapps/common/KoboldKare"
MGMT="$KDIR/KoboldKare_Data/Managed"
CORE="$KDIR/BepInEx/core"
cd "$SCRIPT_DIR"

COUNT="${1:-1}"   # how many instances to build (1..4)

build() {
  local n="$1" out define
  if [ "$n" = "1" ]; then out="KKLLMNPC.dll";  define=""; else out="KKLLMNPC${n}.dll"; define="-define:KK_INSTANCE_${n}"; fi
  mcs -target:library $define -out:"$out" src/*.cs \
    -r:"$CORE/BepInEx.dll" \
    -r:"$CORE/0Harmony.dll" \
    -r:"$MGMT/UnityEngine.dll" \
    -r:"$MGMT/UnityEngine.CoreModule.dll" \
    -r:"$MGMT/UnityEngine.UI.dll" \
    -r:"$MGMT/UnityEngine.UIModule.dll" \
    -r:"$MGMT/UnityEngine.PhysicsModule.dll" \
    -r:"$MGMT/UnityEngine.AnimationModule.dll" \
    -r:"$MGMT/UnityEngine.IMGUIModule.dll" \
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
    -r:"$MGMT/KoboldKare.ISavable.dll"
  cp "$out" "$KDIR/BepInEx/plugins/$out"
  printf 'Built and deployed %s\n' "$KDIR/BepInEx/plugins/$out"
}

for i in $(seq 1 "$COUNT"); do build "$i"; done
