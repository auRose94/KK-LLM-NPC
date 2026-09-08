#!/usr/bin/env bash
# Build KKLLMNPC.dll against BepInEx + UnityEngine + Photon + Assembly-CSharp.
# Source is split across src/*.cs (plugin LLMNPCPlugin + partial class NPCInstance).
#
# Usage:
#   ./build.sh                          # Uses KOBOLDKARE_DIR env var or default
#   KOBOLDKARE_DIR=/path/to/KK        ./build.sh
#
# Environment:
#   KOBOLDKARE_DIR  - Path to the KoboldKare game root
#   KK_COMPILER     - Compiler to use (mcs or dotnet)
#
# Cross-platform notes:
#   Linux/macOS: requires mono-complete (mcs) or dotnet-sdk
#   Windows: requires Mono or .NET SDK installed
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
KDIR="${KOBOLDKARE_DIR:-/mnt/gaming/SteamLibrary/steamapps/common/KoboldKare}"
MGMT="$KDIR/KoboldKare_Data/Managed"
CORE="$KDIR/BepInEx/core"
cd "$SCRIPT_DIR"

# Detect compiler
COMPILER="${KK_COMPILER:-mcs}"
if command -v "$COMPILER" &>/dev/null; then
    : # compiler found
elif command -v mcs &>/dev/null; then
    COMPILER="mcs"
elif command -v dotnet &>/dev/null; then
    COMPILER="dotnet"
else
    echo "ERROR: No C# compiler found. Install mono-complete (mcs) or dotnet-sdk." >&2
    exit 1
fi

if [ ! -d "$KDIR" ]; then
    echo "ERROR: KoboldKare not found at $KDIR" >&2
    echo "Set KOBOLDKARE_DIR to your game install path." >&2
    exit 1
fi

# Verify required assemblies exist
for dep in BepInEx.dll 0Harmony.dll Assembly-CSharp.dll; do
    if [ ! -f "$CORE/$dep" ] && [ ! -f "$MGMT/$dep" ]; then
        echo "WARNING: $dep not found — build may fail without it." >&2
    fi
done

if [ "$COMPILER" = "mcs" ]; then
    # Mono compiler path
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
    printf 'Built with mcs and deployed %s\n' "$KDIR/BepInEx/plugins/KKLLMNPC.dll"
elif [ "$COMPILER" = "dotnet" ]; then
    # .NET SDK path — requires a .csproj (create one if missing)
    if [ ! -f "KKLLMNPC.csproj" ]; then
        cat > KKLLMNPC.csproj <<'PROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net472</TargetFramework>
    <LangVersion>latest</LangVersion>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="BepInEx">
      <HintPath>$(KOBOLDKARE_DIR)/BepInEx/core/BepInEx.dll</HintPath>
    </Reference>
    <Reference Include="0Harmony">
      <HintPath>$(KOBOLDKARE_DIR)/BepInEx/core/0Harmony.dll</HintPath>
    </Reference>
    <Reference Include="Assembly-CSharp">
      <HintPath>$(KOBOLDKARE_DIR)/KoboldKare_Data/Managed/Assembly-CSharp.dll</HintPath>
    </Reference>
    <Reference Include="PhotonUnityNetworking">
      <HintPath>$(KOBOLDKARE_DIR)/KoboldKare_Data/Managed/PhotonUnityNetworking.dll</HintPath>
    </Reference>
    <Reference Include="Photon3Unity3D">
      <HintPath>$(KOBOLDKARE_DIR)/KoboldKare_Data/Managed/Photon3Unity3D.dll</HintPath>
    </Reference>
    <Reference Include="PhotonRealtime">
      <HintPath>$(KOBOLDKARE_DIR)/KoboldKare_Data/Managed/PhotonRealtime.dll</HintPath>
    </Reference>
    <Reference Include="KoboldKare.ISavable">
      <HintPath>$(KOBOLDKARE_DIR)/KoboldKare_Data/Managed/KoboldKare.ISavable.dll</HintPath>
    </Reference>
  </ItemGroup>
</Project>
PROJ
        echo "Created KKLLMNPC.csproj (edit to match your assembly paths)."
    fi
    dotnet build -c Release -o "$KDIR/BepInEx/plugins/"
    echo "Built with dotnet and deployed to $KDIR/BepInEx/plugins/"
else
    echo "ERROR: Unknown compiler '$COMPILER'" >&2
    exit 1
fi
