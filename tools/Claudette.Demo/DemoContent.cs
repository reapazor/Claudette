using System.Diagnostics;
using Claudette.Core.Diffs;
using Claudette.Core.Sessions;
using Claudette.Core.Settings;
using Claudette.Core.Threads;

namespace Claudette.Demo;

/// <summary>
/// The demo's projects and tabs: three game projects in git repositories (Unreal in C++, Unity in C#, Godot), with
/// Claude's changes in their working trees, and seven sessions over them, each with its transcript. The first tab,
/// Grappling hook, is the one the main screenshot shows; Move to the Input System is a thread with two sub-threads, a
/// plan, a task list part-way done and three subagents.
/// </summary>
internal static class DemoContent
{
    /// <summary>Writes the projects under <paramref name="projects"/> and the transcripts to <paramref name="transcripts"/>.</summary>
    /// <returns>The tabs, the one to show first.</returns>
    public static List<TabState> Write(string projects, string transcripts)
    {
        var starfall = Starfall(projects);
        var tidepool = Tidepool(projects);
        var orbit = Orbit(projects);

        var (grapple, firstWrite) = Grapple(starfall);
        var stamina = Stamina(starfall);
        var (inventory, inventoryChanges) = Inventory(tidepool);
        var (input, sentAt) = InputSystem(tidepool);
        var pool = Pool(orbit);
        return
        [
            Tab("tab-grapple", starfall, grapple, "Grappling hook", reviewed: [(Path.Combine(starfall, "Source", "Starfall", "Abilities", "GrappleComponent.h"), firstWrite)]),
            Tab("tab-stamina", starfall, stamina, "Stamina drains twice on clients", mark: TabMark.Question, model: "Sonnet 5.5", effort: "low"),
            Tab("tab-inventory", tidepool, inventory, "Inventory loses items on save", mark: TabMark.Star, reviewed: inventoryChanges),
            Tab("tab-input", tidepool, input, InputThread, mark: TabMark.Flag, thread: true),
            Tab("tab-ui", tidepool, UiInputModules(tidepool, sentAt), UiSubThread, effort: "medium", threadId: "tab-input"),
            Tab("tab-rumble", tidepool, Rumble(tidepool, sentAt), RumbleSubThread, model: "Sonnet 5.5", effort: "medium", threadId: "tab-input"),
            Tab("tab-pool", orbit, pool, "Pool particle emitters", mark: TabMark.Pause),
        ];

        TabState Tab(string id, string folder, DemoTranscript transcript, string name, TabMark? mark = null,
            (string Path, string Change)[]? reviewed = null, string model = "Opus 5.5", string effort = "high", bool thread = false, string? threadId = null) => new()
            {
                Id = id,
                Folder = folder,
                SessionId = transcript.SessionId,
                TranscriptPath = transcript.Save(transcripts),
                UserName = name,
                Mark = mark is { } chosen ? TabMarks.Key(chosen) : null,
                Overrides = new TabOverrides { Model = model, Effort = effort },
                Tokens = new TokenTotals
                {
                    Turns = 3,
                    Models = { ["claude-opus-5-5"] = new ModelTokenTotals { Input = 1240, Output = 9850, CacheWrite = 38200, CacheRead = 412600, EstimatedCostUsd = 1.42 } },
                },
                ReviewedFiles = [.. (reviewed ?? []).Select(r => new ReviewedFile { Path = r.Path, Change = r.Change })],
                // A thread whose Claude was told about its sub-threads (DESIGN.md §18, "Threads").
                IsThread = thread,
                ThreadNote = thread ? $"{UiSubThread}\n{RumbleSubThread}" : null,
                ThreadId = threadId,
            };
    }

    // ---- starfall (Unreal Engine, C++): the grappling hook ---------------------------------------------------------

    private const string UProject = """
        {
            "FileVersion": 3,
            "EngineAssociation": "5.6",
            "Category": "",
            "Description": "",
            "Modules": [
                {
                    "Name": "Starfall",
                    "Type": "Runtime",
                    "LoadingPhase": "Default",
                    "AdditionalDependencies": [ "Engine", "EnhancedInput" ]
                }
            ],
            "Plugins": [
                { "Name": "EnhancedInput", "Enabled": true }
            ]
        }

        """;

    private const string BuildRules = """
        using UnrealBuildTool;

        public class Starfall : ModuleRules
        {
            public Starfall(ReadOnlyTargetRules Target) : base(Target)
            {
                PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;
                PublicDependencyModuleNames.AddRange(new[] { "Core", "CoreUObject", "Engine", "InputCore", "EnhancedInput" });
            }
        }

        """;

    private const string CharacterHeader = """
        #pragma once

        #include "CoreMinimal.h"
        #include "GameFramework/Character.h"
        #include "StarfallCharacter.generated.h"

        class UInputAction;
        struct FInputActionValue;

        UCLASS()
        class STARFALL_API AStarfallCharacter : public ACharacter
        {
            GENERATED_BODY()

        public:
            AStarfallCharacter();

        protected:
            virtual void SetupPlayerInputComponent(UInputComponent* PlayerInputComponent) override;

            void Move(const FInputActionValue& Value);
            void Look(const FInputActionValue& Value);
            void Dash();

            UPROPERTY(EditDefaultsOnly, Category = "Input")
            TObjectPtr<UInputAction> MoveAction;

            UPROPERTY(EditDefaultsOnly, Category = "Input")
            TObjectPtr<UInputAction> LookAction;

            UPROPERTY(EditDefaultsOnly, Category = "Input")
            TObjectPtr<UInputAction> JumpAction;

            UPROPERTY(EditDefaultsOnly, Category = "Input")
            TObjectPtr<UInputAction> DashAction;
        };

        """;

    private const string MembersOld = """
            UPROPERTY(EditDefaultsOnly, Category = "Input")
            TObjectPtr<UInputAction> DashAction;
        };
        """;

    private const string MembersNew = """
            UPROPERTY(EditDefaultsOnly, Category = "Input")
            TObjectPtr<UInputAction> DashAction;

            UPROPERTY(EditDefaultsOnly, Category = "Input")
            TObjectPtr<UInputAction> GrappleAction;

            UPROPERTY(VisibleAnywhere, Category = "Abilities")
            TObjectPtr<class UGrappleComponent> Grapple;
        };
        """;

    private const string Character = """
        #include "Player/StarfallCharacter.h"
        #include "EnhancedInputComponent.h"
        #include "GameFramework/CharacterMovementComponent.h"
        #include "InputActionValue.h"

        AStarfallCharacter::AStarfallCharacter()
        {
            GetCharacterMovement()->AirControl = 0.35f;
            GetCharacterMovement()->JumpZVelocity = 700.f;
        }

        void AStarfallCharacter::SetupPlayerInputComponent(UInputComponent* PlayerInputComponent)
        {
            Super::SetupPlayerInputComponent(PlayerInputComponent);

            if (UEnhancedInputComponent* Input = Cast<UEnhancedInputComponent>(PlayerInputComponent))
            {
                Input->BindAction(MoveAction, ETriggerEvent::Triggered, this, &AStarfallCharacter::Move);
                Input->BindAction(LookAction, ETriggerEvent::Triggered, this, &AStarfallCharacter::Look);
                Input->BindAction(JumpAction, ETriggerEvent::Started, this, &ACharacter::Jump);
                Input->BindAction(DashAction, ETriggerEvent::Started, this, &AStarfallCharacter::Dash);
            }
        }

        void AStarfallCharacter::Move(const FInputActionValue& Value)
        {
            const FVector2D Axis = Value.Get<FVector2D>();
            AddMovementInput(GetActorForwardVector(), Axis.Y);
            AddMovementInput(GetActorRightVector(), Axis.X);
        }

        void AStarfallCharacter::Look(const FInputActionValue& Value)
        {
            const FVector2D Axis = Value.Get<FVector2D>();
            AddControllerYawInput(Axis.X);
            AddControllerPitchInput(Axis.Y);
        }

        void AStarfallCharacter::Dash()
        {
            LaunchCharacter(GetActorForwardVector() * 1200.f, true, false);
        }

        """;

    private const string IncludeOld = "#include \"Player/StarfallCharacter.h\"";

    private const string IncludeNew = """
        #include "Player/StarfallCharacter.h"
        #include "Abilities/GrappleComponent.h"
        """;

    private const string ConstructorOld = """
            GetCharacterMovement()->AirControl = 0.35f;
            GetCharacterMovement()->JumpZVelocity = 700.f;
        }
        """;

    private const string ConstructorNew = """
            // Enough air control to steer while the grapple pulls.
            GetCharacterMovement()->AirControl = 0.6f;
            GetCharacterMovement()->JumpZVelocity = 700.f;

            Grapple = CreateDefaultSubobject<UGrappleComponent>(TEXT("Grapple"));
        }
        """;

    private const string BindingsOld = """
                Input->BindAction(DashAction, ETriggerEvent::Started, this, &AStarfallCharacter::Dash);
        """;

    private const string BindingsNew = """
                Input->BindAction(DashAction, ETriggerEvent::Started, this, &AStarfallCharacter::Dash);
                Input->BindAction(GrappleAction, ETriggerEvent::Started, Grapple.Get(), &UGrappleComponent::Fire);
                Input->BindAction(JumpAction, ETriggerEvent::Started, Grapple.Get(), &UGrappleComponent::Release);
        """;

    private const string GrappleHeader = """
        #pragma once

        #include "CoreMinimal.h"
        #include "Components/ActorComponent.h"
        #include "GrappleComponent.generated.h"

        /** Fires a grapple along the camera's aim and pulls the owning character toward where it hits. */
        UCLASS(ClassGroup = (Abilities), meta = (BlueprintSpawnableComponent))
        class STARFALL_API UGrappleComponent : public UActorComponent
        {
            GENERATED_BODY()

        public:
            UGrappleComponent();

            /** Traces along the view and, on a hit within range, anchors there and starts pulling. */
            void Fire();

            /** Lets go of the anchor; jumping calls this. */
            void Release();

            bool IsAttached() const { return bAttached; }

            virtual void TickComponent(float DeltaTime, ELevelTick TickType, FActorComponentTickFunction* ThisTickFunction) override;

        protected:
            UPROPERTY(EditDefaultsOnly, Category = "Grapple")
            float Range = 2500.f;

            UPROPERTY(EditDefaultsOnly, Category = "Grapple")
            float PullStrength = 380000.f;

            /** Within this distance of the anchor the grapple lets go by itself. */
            UPROPERTY(EditDefaultsOnly, Category = "Grapple")
            float ReleaseDistance = 150.f;

        private:
            FVector Anchor = FVector::ZeroVector;
            bool bAttached = false;
        };

        """;

    private const string GrappleSource = """
        #include "Abilities/GrappleComponent.h"
        #include "GameFramework/Character.h"
        #include "GameFramework/CharacterMovementComponent.h"

        UGrappleComponent::UGrappleComponent()
        {
            PrimaryComponentTick.bCanEverTick = true;
            PrimaryComponentTick.bStartWithTickEnabled = false;
        }

        void UGrappleComponent::Fire()
        {
            ACharacter* Owner = CastChecked<ACharacter>(GetOwner());
            FVector ViewLocation;
            FRotator ViewRotation;
            Owner->GetActorEyesViewPoint(ViewLocation, ViewRotation);

            FHitResult Hit;
            const FCollisionQueryParams Params(SCENE_QUERY_STAT(Grapple), false, Owner);
            const FVector End = ViewLocation + ViewRotation.Vector() * Range;
            if (GetWorld()->LineTraceSingleByChannel(Hit, ViewLocation, End, ECC_Visibility, Params))
            {
                Anchor = Hit.ImpactPoint;
                bAttached = true;
                SetComponentTickEnabled(true);
            }
        }

        void UGrappleComponent::Release()
        {
            bAttached = false;
            SetComponentTickEnabled(false);
        }

        void UGrappleComponent::TickComponent(float DeltaTime, ELevelTick TickType, FActorComponentTickFunction* ThisTickFunction)
        {
            Super::TickComponent(DeltaTime, TickType, ThisTickFunction);

            ACharacter* Owner = CastChecked<ACharacter>(GetOwner());
            const FVector ToAnchor = Anchor - Owner->GetActorLocation();
            if (ToAnchor.Size() < ReleaseDistance)
            {
                Release();
                return;
            }
            Owner->GetCharacterMovement()->AddForce(ToAnchor.GetSafeNormal() * PullStrength);
        }

        """;

    private const string GrappleTest = """
        #include "Abilities/GrappleComponent.h"
        #include "Misc/AutomationTest.h"
        #include "Player/StarfallCharacter.h"
        #include "Tests/AutomationEditorCommon.h"

        IMPLEMENT_SIMPLE_AUTOMATION_TEST(FGrappleMissTest, "Starfall.Abilities.Grapple.MissesBeyondRange",
            EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

        bool FGrappleMissTest::RunTest(const FString& Parameters)
        {
            UWorld* World = FAutomationEditorCommonUtils::CreateNewMap();
            AStarfallCharacter* Character = World->SpawnActor<AStarfallCharacter>();
            UGrappleComponent* Grapple = Character->FindComponentByClass<UGrappleComponent>();

            // An empty map: nothing to hit, so the grapple doesn't attach.
            Grapple->Fire();
            TestFalse(TEXT("Attached with nothing in range"), Grapple->IsAttached());
            return true;
        }

        IMPLEMENT_SIMPLE_AUTOMATION_TEST(FGrappleReleaseTest, "Starfall.Abilities.Grapple.ReleaseDetaches",
            EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

        bool FGrappleReleaseTest::RunTest(const FString& Parameters)
        {
            UWorld* World = FAutomationEditorCommonUtils::CreateNewMap();
            AStarfallCharacter* Character = World->SpawnActor<AStarfallCharacter>();
            UGrappleComponent* Grapple = Character->FindComponentByClass<UGrappleComponent>();

            Grapple->Release();
            TestFalse(TEXT("Attached after release"), Grapple->IsAttached());
            return true;
        }

        """;

    private const string StaminaSource = """
        #include "Player/StaminaComponent.h"

        void UStaminaComponent::Spend(float Amount)
        {
            Current = FMath::Max(0.f, Current - Amount);
            OnStaminaChanged.Broadcast(Current, Max);
        }

        """;

    private const string SpendOld = "    Current = FMath::Max(0.f, Current - Amount);";

    private const string SpendNew = """
            // The server owns stamina and replicates it: a client spending it too took it off twice.
            if (!GetOwner()->HasAuthority())
            {
                return;
            }
            Current = FMath::Max(0.f, Current - Amount);
        """;

    private const string Engine = "C:/Program Files/Epic Games/UE_5.6/Engine";

    private static string Starfall(string projects)
    {
        var player = "Source/Starfall/Player/";
        return Project(projects, "starfall", "feature/grapple",
            new()
            {
                ["Starfall.uproject"] = UProject,
                ["Source/Starfall/Starfall.Build.cs"] = BuildRules,
                [player + "StarfallCharacter.h"] = CharacterHeader,
                [player + "StarfallCharacter.cpp"] = Character,
                [player + "StaminaComponent.cpp"] = StaminaSource,
            },
            new()
            {
                [player + "StarfallCharacter.h"] = CharacterHeader.Replace(MembersOld, MembersNew),
                [player + "StarfallCharacter.cpp"] = Character.Replace(IncludeOld, IncludeNew).Replace(ConstructorOld, ConstructorNew).Replace(BindingsOld, BindingsNew),
                [player + "StaminaComponent.cpp"] = StaminaSource.Replace(SpendOld, SpendNew),
                ["Source/Starfall/Abilities/GrappleComponent.h"] = GrappleHeader,
                ["Source/Starfall/Abilities/GrappleComponent.cpp"] = GrappleSource,
                ["Source/Starfall/Tests/GrappleComponentTest.cpp"] = GrappleTest,
            });
    }

    private static (DemoTranscript Transcript, string FirstWrite) Grapple(string root)
    {
        var source = Path.Combine(root, "Source", "Starfall");
        var abilities = Path.Combine(source, "Abilities");
        var character = Path.Combine(source, "Player", "StarfallCharacter.cpp");
        var t = new DemoTranscript("5f6b2c1e-7d0a-4c55-9a51-2b8e6f0c4d11", root, "feature/grapple", Today(9, 12))
            .Prompt("Add a grappling hook to the player. It should fire along the camera's aim, pull the character toward where it hits, and let go when you jump. Keep it in its own component, and add an automation test.")
            .Thinking("The character binds its input in AStarfallCharacter::SetupPlayerInputComponent. A UActorComponent for the grapple keeps the character class small; the pull can be a force each tick toward the anchor, so air control still works.")
            .Text("I'll start by finding where the character binds its abilities.")
            .Grep("BindAction", "Source/Starfall", "Source/Starfall/Player/StarfallCharacter.cpp", "Source/Starfall/Player/StarfallPlayerController.cpp")
            .Read(character, Character)
            .Text("`AStarfallCharacter` binds Move, Look, Jump and Dash with Enhanced Input. I'll add a `UGrappleComponent` that does the trace and the pull, and bind it to a new `GrappleAction`.");
        var firstWrite = t.Create(Path.Combine(abilities, "GrappleComponent.h"), GrappleHeader);
        t.Create(Path.Combine(abilities, "GrappleComponent.cpp"), GrappleSource);
        t.Edit(Path.Combine(source, "Player", "StarfallCharacter.h"), CharacterHeader, MembersOld, MembersNew);
        var edited = Character;
        foreach (var (before, after) in new[] { (IncludeOld, IncludeNew), (ConstructorOld, ConstructorNew), (BindingsOld, BindingsNew) })
        {
            t.Edit(character, edited, before, after);
            edited = edited.Replace(before, after);
        }
        t.Create(Path.Combine(source, "Tests", "GrappleComponentTest.cpp"), GrappleTest);
        t.Bash($"\"{Engine}/Build/BatchFiles/Build.bat\" StarfallEditor Win64 Development -Project=\"$PWD/Starfall.uproject\" -WaitMutex", "Build the editor target", """
            Using bundled DotNet SDK version: 8.0.300
            Building StarfallEditor...
            [1/6] Compile [x64] GrappleComponent.cpp
            [2/6] Compile [x64] GrappleComponentTest.cpp
            [3/6] Compile [x64] StarfallCharacter.cpp
            [4/6] Compile [x64] Module.Starfall.gen.cpp
            [5/6] Link [x64] UnrealEditor-Starfall.dll
            [6/6] WriteMetadata StarfallEditor.target
            Total time in Parallel executor: 41.37 seconds
            Result: Succeeded
            """)
            .Bash($"\"{Engine}/Binaries/Win64/UnrealEditor-Cmd.exe\" Starfall.uproject -ExecCmds=\"Automation RunTests Starfall.Abilities.Grapple; Quit\" -unattended -nullrhi -nosplash", "Run the grapple's automation tests", """
            LogAutomationController: Display: Test Completed. Result={Success} Name={MissesBeyondRange} Path={Starfall.Abilities.Grapple.MissesBeyondRange}
            LogAutomationController: Display: Test Completed. Result={Success} Name={ReleaseDetaches} Path={Starfall.Abilities.Grapple.ReleaseDetaches}
            LogAutomationController: Display: **** TEST COMPLETE. EXIT CODE: 0 ****
            """)
            .Text("""
                The grappling hook is in, as its own component.

                **What changed**
                - `UGrappleComponent` traces along the camera's aim up to `Range` (25 m) and, on a hit, anchors there and pulls the character toward it. It lets go by itself within 1.5 m of the anchor.
                - `AStarfallCharacter` creates the component and binds a new `GrappleAction` to fire it. Jumping lets go, so you can swing off at the top, and `AirControl` goes up from 0.35 to 0.6 so you can steer on the line.
                - `GrappleComponentTest.cpp` covers a miss and a release.

                The pull is a force each tick rather than a launch, so air control still works while you're on the line:

                ```cpp
                const FVector ToAnchor = Anchor - Owner->GetActorLocation();
                Owner->GetCharacterMovement()->AddForce(ToAnchor.GetSafeNormal() * PullStrength);
                ```

                The editor target builds and both automation tests pass. To try it, create an `IA_Grapple` input action in the editor and assign it to **Grapple Action** on `BP_StarfallCharacter`.
                """, last: true)
            .Title("Grappling hook");
        return (t, firstWrite);
    }

    private static DemoTranscript Stamina(string root)
    {
        var t = new DemoTranscript("8a1d4e77-3b2c-4f10-8e6d-91c0b7a5e222", root, "feature/grapple", Today(8, 40))
            .Prompt("In multiplayer, stamina drains twice as fast on clients as on the host. Can you find out why?")
            .Text("Dashing calls `Spend` on both the client and the server, and the server's value then replicates back on top. I'll have only the server spend it.");
        t.Edit(Path.Combine(root, "Source", "Starfall", "Player", "StaminaComponent.cpp"), StaminaSource, SpendOld, SpendNew);
        return t.Text("Fixed: only the server spends stamina now, and clients get it by replication. Should the dash itself wait for the server too, or keep predicting it on the client?", last: true);
    }

    // ---- tidepool (Unity, C#): the Input System thread, and the inventory fix ---------------------------------------

    private const string InputThread = "Move to the Input System";

    private const string UiSubThread = "UI on the Input System";

    private const string RumbleSubThread = "Gamepad rumble";

    private const string ProjectVersion = """
        m_EditorVersion: 6000.2.6f1
        m_EditorVersionWithRevision: 6000.2.6f1 (4a5c79bdd8e2)

        """;

    private const string Manifest = """
        {
          "dependencies": {
            "com.unity.cinemachine": "3.1.4",
            "com.unity.render-pipelines.universal": "17.2.0",
            "com.unity.test-framework": "1.5.1",
            "com.unity.ugui": "2.0.0"
          }
        }

        """;

    private const string PackagesOld = "    \"com.unity.cinemachine\": \"3.1.4\",";

    private const string PackagesNew = """
            "com.unity.cinemachine": "3.1.4",
            "com.unity.inputsystem": "1.14.2",
        """;

    private const string PlayerControls = """
        {
            "name": "PlayerControls",
            "maps": [
                {
                    "name": "Player",
                    "actions": [
                        { "name": "Move", "type": "Value", "expectedControlType": "Vector2" },
                        { "name": "Look", "type": "Value", "expectedControlType": "Vector2" },
                        { "name": "Jump", "type": "Button" },
                        { "name": "Cast", "type": "Button" }
                    ],
                    "bindings": [
                        { "name": "WASD", "path": "2DVector", "action": "Move", "isComposite": true },
                        { "name": "up", "path": "<Keyboard>/w", "action": "Move", "isPartOfComposite": true },
                        { "name": "down", "path": "<Keyboard>/s", "action": "Move", "isPartOfComposite": true },
                        { "name": "left", "path": "<Keyboard>/a", "action": "Move", "isPartOfComposite": true },
                        { "name": "right", "path": "<Keyboard>/d", "action": "Move", "isPartOfComposite": true },
                        { "path": "<Gamepad>/leftStick", "action": "Move" },
                        { "path": "<Mouse>/delta", "action": "Look" },
                        { "path": "<Gamepad>/rightStick", "action": "Look" },
                        { "path": "<Keyboard>/space", "action": "Jump" },
                        { "path": "<Gamepad>/buttonSouth", "action": "Jump" },
                        { "path": "<Mouse>/leftButton", "action": "Cast" },
                        { "path": "<Gamepad>/rightTrigger", "action": "Cast" }
                    ]
                }
            ]
        }

        """;

    private const string PlayerController = """
        using UnityEngine;

        namespace Tidepool.Player
        {
            [RequireComponent(typeof(CharacterController))]
            public class PlayerController : MonoBehaviour
            {
                [SerializeField] private float moveSpeed = 4.5f;
                [SerializeField] private float jumpHeight = 1.2f;

                private CharacterController _controller;
                private Vector3 _velocity;

                private void Awake() => _controller = GetComponent<CharacterController>();

                private void Update()
                {
                    var move = new Vector3(Input.GetAxis("Horizontal"), 0f, Input.GetAxis("Vertical"));
                    _controller.Move(transform.TransformDirection(move) * (moveSpeed * Time.deltaTime));

                    if (_controller.isGrounded && Input.GetButtonDown("Jump"))
                    {
                        _velocity.y = Mathf.Sqrt(jumpHeight * -2f * Physics.gravity.y);
                    }
                    _velocity.y += Physics.gravity.y * Time.deltaTime;
                    _controller.Move(_velocity * Time.deltaTime);
                }
            }
        }

        """;

    private const string ControllerOld = """
                private void Awake() => _controller = GetComponent<CharacterController>();

                private void Update()
                {
                    var move = new Vector3(Input.GetAxis("Horizontal"), 0f, Input.GetAxis("Vertical"));
                    _controller.Move(transform.TransformDirection(move) * (moveSpeed * Time.deltaTime));

                    if (_controller.isGrounded && Input.GetButtonDown("Jump"))
        """;

    private const string ControllerNew = """
                private PlayerControls _controls;

                private void Awake()
                {
                    _controller = GetComponent<CharacterController>();
                    _controls = new PlayerControls();
                }

                private void OnEnable() => _controls.Player.Enable();

                private void OnDisable() => _controls.Player.Disable();

                private void Update()
                {
                    var input = _controls.Player.Move.ReadValue<Vector2>();
                    var move = new Vector3(input.x, 0f, input.y);
                    _controller.Move(transform.TransformDirection(move) * (moveSpeed * Time.deltaTime));

                    if (_controller.isGrounded && _controls.Player.Jump.WasPressedThisFrame())
        """;

    private const string InventorySave = """
        using System.IO;
        using UnityEngine;

        namespace Tidepool.Inventory
        {
            public static class InventorySave
            {
                private static string SavePath => Path.Combine(Application.persistentDataPath, "inventory.json");

                public static void Save(Inventory inventory)
                {
                    var json = JsonUtility.ToJson(inventory.ToData());
                    File.WriteAllText(SavePath, json);
                }
            }
        }

        """;

    private const string SaveOld = "            File.WriteAllText(SavePath, json);";

    private const string SaveNew = """
                    // Written whole, then swapped in: a save cut short by a scene change leaves the last good file.
                    var temp = SavePath + ".tmp";
                    File.WriteAllText(temp, json);
                    if (File.Exists(SavePath))
                    {
                        File.Replace(temp, SavePath, null);
                    }
                    else
                    {
                        File.Move(temp, SavePath);
                    }
        """;

    private const string InventorySaveTests = """
        using System.IO;
        using NUnit.Framework;
        using UnityEngine;

        namespace Tidepool.Inventory.Tests
        {
            public class InventorySaveTests
            {
                private static string SavePath => Path.Combine(Application.persistentDataPath, "inventory.json");

                [Test]
                public void A_save_replaces_the_last_one_whole()
                {
                    var inventory = new Inventory();
                    inventory.Add("Rusty hook", 1);
                    InventorySave.Save(inventory);

                    inventory.Add("Silver lure", 2);
                    InventorySave.Save(inventory);

                    StringAssert.Contains("Silver lure", File.ReadAllText(SavePath));
                    Assert.IsFalse(File.Exists(SavePath + ".tmp"));
                }
            }
        }

        """;

    private const string PlanBefore = """
        ## Move Tidepool to the Input System

        1. Add the Input System package, and set **Active Input Handling** to **Both** while the move is under way.
        2. Create a `PlayerControls` input actions asset: Move, Look, Jump and Cast, for keyboard and mouse and for gamepad.
        3. Move `PlayerController`, `CameraOrbit` and `CastingRod` to the generated `PlayerControls` class.
        4. Swap each scene's `StandaloneInputModule` for `InputSystemUIInputModule`, and the pause menu's too.
        5. Run the EditMode and PlayMode tests.
        6. Set **Active Input Handling** to **Input System Package (New)**.
        """;

    private const string PlanApproved = """
        ## Move Tidepool to the Input System

        1. Add the Input System package, and set **Active Input Handling** to **Both** while the move is under way.
        2. Create a `PlayerControls` input actions asset: Move, Look, Jump and Cast, for keyboard and mouse and for gamepad.
        3. Move `PlayerController`, `CameraOrbit` and `CastingRod` to the generated `PlayerControls` class.
        4. **Sub-thread *UI on the Input System*:** swap each scene's `StandaloneInputModule` for `InputSystemUIInputModule`, and the pause menu's too.
        5. **Sub-thread *Gamepad rumble*:** rumble the gamepad when a fish bites and when the line snaps.
        6. Run the EditMode and PlayMode tests.
        7. Set **Active Input Handling** to **Input System Package (New)**.

        Steps 4 and 5 need only step 2, so they run alongside step 3.
        """;

    private const string UiMessage = """
        Task #4 of the Input System move: swap the `StandaloneInputModule` on the EventSystem in `Title.unity`, `Harbor.unity` and `Reef.unity`, and in the `PauseMenu` prefab, for `InputSystemUIInputModule`, using the UI map of `Assets/Input/PlayerControls.inputactions`. Keep the pause menu's extra submit button (`Submit2`) as a second binding on Submit.
        """;

    private const string RumbleMessage = """
        Task #5 of the Input System move: rumble the gamepad with `Gamepad.current.SetMotorSpeeds` when a fish bites (`FishingLine.OnBite`) and when the line snaps (`FishingLine.OnSnap`): a short low rumble for a bite, a sharp one for a snap, and none while the game is paused. `PlayerControls` is in `Assets/Input/`.
        """;

    private static readonly string[] OldInputScripts =
        ["Assets/Scripts/Player/PlayerController.cs", "Assets/Scripts/Player/CameraOrbit.cs", "Assets/Scripts/Fishing/CastingRod.cs", "Assets/Scripts/UI/PauseMenu.cs"];

    private static readonly string[] InputModules =
        ["Assets/Scenes/Harbor.unity", "Assets/Scenes/Reef.unity", "Assets/Scenes/Title.unity", "Assets/Prefabs/UI/PauseMenu.prefab"];

    private static string Tidepool(string projects) => Project(projects, "tidepool", "feature/input-system",
        new()
        {
            ["ProjectSettings/ProjectVersion.txt"] = ProjectVersion,
            ["Packages/manifest.json"] = Manifest,
            ["Assets/Scripts/Player/PlayerController.cs"] = PlayerController,
            ["Assets/Scripts/Inventory/InventorySave.cs"] = InventorySave,
        },
        new()
        {
            ["Packages/manifest.json"] = Manifest.Replace(PackagesOld, PackagesNew),
            ["Assets/Input/PlayerControls.inputactions"] = PlayerControls,
            ["Assets/Scripts/Player/PlayerController.cs"] = PlayerController.Replace(ControllerOld, ControllerNew),
            ["Assets/Scripts/Inventory/InventorySave.cs"] = InventorySave.Replace(SaveOld, SaveNew),
            ["Assets/Tests/EditMode/InventorySaveTests.cs"] = InventorySaveTests,
        });

    private static (DemoTranscript Transcript, (string Path, string Change)[] Reviewed) Inventory(string root)
    {
        var save = Path.Combine(root, "Assets", "Scripts", "Inventory", "InventorySave.cs");
        var tests = Path.Combine(root, "Assets", "Tests", "EditMode", "InventorySaveTests.cs");
        var t = new DemoTranscript("c47e9a02-51d8-4b6e-a3f2-0e9d8c7b6a33", root, "feature/input-system", Today(7, 55))
            .Prompt("Players say they sometimes lose their whole inventory when the game saves during a scene change. Can you find out why?")
            .Text("`InventorySave.Save` writes straight over `inventory.json`. When a scene change cuts it short, the file is left half-written and the next load can't parse it. I'll write to a temporary file and swap it in.");
        var edit = t.Edit(save, InventorySave, SaveOld, SaveNew);
        var write = t.Create(tests, InventorySaveTests);
        t.Text("A save can't leave a half-written file now: the new one is written whole, then replaces the old. The test checks that a second save replaces the first and leaves no temporary file behind.", last: true);
        return (t, [(save, edit), (tests, write)]);
    }

    /// <returns>The thread's transcript, and when it sent its sub-threads their work.</returns>
    private static (DemoTranscript Transcript, DateTimeOffset SentAt) InputSystem(string root)
    {
        var scripts = Path.Combine(root, "Assets", "Scripts");
        var t = new DemoTranscript("e2b5d8f1-9c3a-4d7e-b6a0-4f1e2d3c5b44", root, "feature/input-system", Today(7, 20))
            .Prompt("Move the game from the old Input Manager to the Input System package, so gamepads work properly and players can rebind. Plan it first: I want to see what it touches before anything changes.")
            .Text("I'll look at three things in parallel: the scripts that read input, the UI's event systems, and the packages and settings.")
            .Agents(
                new DemoAgent("Find the old Input calls", "Explore",
                    "Find every use of the old Input Manager (`Input.GetAxis`, `Input.GetButton*`, `Input.GetKey*`, `Input.mousePosition`) under Assets/. List each file with the calls it makes.",
                    a => a.Grep(@"Input\.(GetAxis|GetButton|GetKey|mousePosition)", "Assets", OldInputScripts)
                        .Read(Path.Combine(scripts, "Player", "PlayerController.cs"), PlayerController),
                    """
                    Four scripts use the old Input Manager:

                    | File | Calls |
                    |---|---|
                    | `Player/PlayerController.cs` | `GetAxis("Horizontal")`, `GetAxis("Vertical")`, `GetButtonDown("Jump")` |
                    | `Player/CameraOrbit.cs` | `GetAxis("Mouse X")`, `GetAxis("Mouse Y")` |
                    | `Fishing/CastingRod.cs` | `GetButtonDown("Fire1")`, `GetButtonUp("Fire1")` |
                    | `UI/PauseMenu.cs` | `GetKeyDown(KeyCode.Escape)` |
                    """),
                new DemoAgent("Check the UI's input modules", "Explore",
                    "Find which scenes and prefabs have an EventSystem with a StandaloneInputModule, which stops working once the old input handler is off.",
                    a => a.Grep("StandaloneInputModule", "Assets", InputModules),
                    "Three scenes (`Title`, `Harbor` and `Reef`) and the `PauseMenu` prefab each have an EventSystem with a `StandaloneInputModule`. Each needs an `InputSystemUIInputModule` instead, with the UI action map. Only the pause menu changes the defaults: it adds a second submit button, `Submit2`."),
                new DemoAgent("Check the packages and settings", "Explore",
                    "Read Packages/manifest.json and ProjectSettings/ProjectSettings.asset. Is the Input System package installed, and which input handler is active?",
                    a => a.Read(Path.Combine(root, "Packages", "manifest.json"), Manifest)
                        .Grep("activeInputHandler", "ProjectSettings", "ProjectSettings/ProjectSettings.asset"),
                    "The Input System package isn't installed: the manifest has Cinemachine, URP, the Test Framework and uGUI. `activeInputHandler` is `0`, the Input Manager only. Setting it to `2` (Both) lets the old and new systems run side by side during the move."))
            .Text("Four scripts, three scenes and a prefab read input the old way, and the package isn't installed yet. Here's the plan.")
            .Plan(PlanBefore, feedback: "Hand the UI and the gamepad rumble to sub-threads, so they run alongside you.")
            .Text("I'll give the UI and the rumble to the sub-threads, and add the rumble as its own step.")
            .Plan(PlanApproved)
            .TaskCreate("1", "Add the Input System package", "Add com.unity.inputsystem to the manifest, and set Active Input Handling to Both.", "Adding the Input System package")
            .TaskCreate("2", "Create the PlayerControls actions", "Move, Look, Jump and Cast, with keyboard and mouse and gamepad bindings.", "Creating the PlayerControls actions")
            .TaskCreate("3", "Move the player scripts to PlayerControls", "PlayerController, CameraOrbit and CastingRod read the generated class instead of Input.", "Moving the player scripts to PlayerControls")
            .TaskCreate("4", "Swap the UI input modules", "InputSystemUIInputModule in Title, Harbor, Reef and the PauseMenu prefab.", "Swapping the UI input modules")
            .TaskCreate("5", "Rumble on bites and snapped lines", "Gamepad.current.SetMotorSpeeds when a fish bites and when the line snaps.", "Adding gamepad rumble")
            .TaskCreate("6", "Run the EditMode and PlayMode tests", "Every test in both modes, with a gamepad and with keyboard and mouse.", "Running the tests")
            .TaskCreate("7", "Turn off the old input handler", "Set Active Input Handling to Input System Package (New).", "Turning off the old input handler")
            .TaskUpdate("4", owner: UiSubThread, blockedBy: ["2"])
            .TaskUpdate("5", owner: RumbleSubThread, blockedBy: ["2"])
            .TaskUpdate("6", blockedBy: ["3", "4", "5"])
            .TaskUpdate("7", blockedBy: ["6"])
            .TaskUpdate("1", "in_progress")
            .Wait(1);
        t.Edit(Path.Combine(root, "Packages", "manifest.json"), Manifest, PackagesOld, PackagesNew);
        t.TaskUpdate("1", "completed")
            .TaskUpdate("2", "in_progress")
            .Wait(3);
        t.Create(Path.Combine(root, "Assets", "Input", "PlayerControls.inputactions"), PlayerControls);
        t.TaskUpdate("2", "completed")
            .Text("The actions exist now, so the UI and the rumble can start. Handing them to the sub-threads.")
            .SendMessage(UiSubThread, "Swap the UI input modules", UiMessage)
            .SendMessage(RumbleSubThread, "Rumble on bites and snapped lines", RumbleMessage);
        var sentAt = t.At;
        t.TaskUpdate("4", "in_progress")
            .TaskUpdate("5", "in_progress")
            .TaskUpdate("3", "in_progress")
            .Wait(2);
        t.Edit(Path.Combine(scripts, "Player", "PlayerController.cs"), PlayerController, ControllerOld, ControllerNew);
        t.Text("""
            #1 and #2 are done, and `PlayerController` now reads `PlayerControls`. `CameraOrbit` and `CastingRod` are next, in #3.

            The sub-threads have the UI (#4) and the rumble (#5), and their results come back here when they finish. Then I'll run the tests (#6) and turn the old input handler off (#7).
            """, last: true)
            .Title(InputThread);
        return (t, sentAt);
    }

    private static DemoTranscript UiInputModules(string root, DateTimeOffset sentAt) =>
        new DemoTranscript("0d3f6a28-4b1e-4c9a-8e2d-7a6b5c4d3e66", root, "feature/input-system", sentAt.AddSeconds(-25))
            .Prompt(ThreadMessages.ForSubThread(InputThread, UiMessage.Trim()))
            .Grep("StandaloneInputModule", "Assets", InputModules)
            .Text("Found it in the three scenes and the `PauseMenu` prefab. Scene files are YAML, so I can swap the component by hand, but the new module's script reference has to match the package's. Shall I do it that way, or would you rather swap them in the editor and have me check the result?", last: true);

    private static DemoTranscript Rumble(string root, DateTimeOffset sentAt) =>
        new DemoTranscript("6c8e1f40-2a5d-4b7c-9d3e-1f0a9b8c7d77", root, "feature/input-system", sentAt.AddSeconds(-20))
            .Prompt(ThreadMessages.ForSubThread(InputThread, RumbleMessage.Trim()))
            .Grep(@"OnBite|OnSnap", "Assets/Scripts", "Assets/Scripts/Fishing/FishingLine.cs", "Assets/Scripts/Fishing/BiteIndicator.cs")
            .Text("`FishingLine` raises `OnBite` and `OnSnap`, so a small `GamepadRumble` component can listen to both. How strong should a snap be? I'd start at 0.8 on the high-frequency motor for a quarter of a second.", last: true);

    // ---- orbit-game (Godot) ----------------------------------------------------------------------------------------

    private const string Effects = "extends Node2D\n\nfunc spawn_burst(at: Vector2) -> void:\n\tvar emitter := BURST.instantiate()\n\tadd_child(emitter)\n\temitter.global_position = at\n";

    private const string SpawnOld = "var emitter := BURST.instantiate()\n\tadd_child(emitter)";

    private const string SpawnNew = "var emitter := _pool.take()";

    private static string Orbit(string projects) => Project(projects, "orbit-game", "main",
        new() { ["project.godot"] = "[application]\nconfig/name=\"Orbit\"\n", ["scripts/effects.gd"] = Effects },
        new() { ["scripts/effects.gd"] = Effects.Replace(SpawnOld, SpawnNew) });

    private static DemoTranscript Pool(string root)
    {
        var t = new DemoTranscript("a93c0f5e-2d1b-4e8a-9f7c-6b5a4d3e2f55", root, "main", Today(6, 5))
            .Prompt("Explosions stutter when lots of them spawn at once. Pool the particle emitters.")
            .Text("Taking emitters from a pool instead of instancing one per burst.");
        t.Edit(Path.Combine(root, "scripts", "effects.gd"), Effects, SpawnOld, SpawnNew);
        return t.Text("Bursts reuse pooled emitters now. I paused before tuning the pool size: how many bursts can be on screen at once?", last: true);
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// A git repository with <paramref name="before"/> committed on main, then <paramref name="branch"/> checked out
    /// with <paramref name="after"/> written over it and left uncommitted, as Claude left it.
    /// </summary>
    private static string Project(string projects, string name, string branch, Dictionary<string, string> before, Dictionary<string, string> after)
    {
        var root = Path.Combine(projects, name);
        Directory.CreateDirectory(root);
        Git(root, "init", "-q", "-b", "main");
        WriteFiles(root, before);
        Git(root, "add", "-A");
        Git(root, "-c", "user.name=Demo", "-c", "user.email=demo@example.com", "commit", "-q", "-m", "Initial commit");
        if (branch != "main")
        {
            Git(root, "checkout", "-q", "-b", branch);
        }
        WriteFiles(root, after);
        return root;
    }

    private static void WriteFiles(string root, Dictionary<string, string> files)
    {
        foreach (var (name, text) in files)
        {
            var path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text.ReplaceLineEndings("\n"));
        }
    }

    private static void Git(string folder, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true };
        // Line endings as written: the files' "before" in the transcripts has to match what's on disk.
        foreach (var arg in (string[])["-c", "core.autocrlf=false", .. args])
        {
            start.ArgumentList.Add(arg);
        }
        using var git = Process.Start(start) ?? throw new InvalidOperationException("git didn't start.");
        var error = git.StandardError.ReadToEnd();
        git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        if (git.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error.Trim()}");
        }
    }

    /// <summary>Earlier today, so the conversation's times read naturally.</summary>
    private static DateTimeOffset Today(int hour, int minute) => new DateTimeOffset(DateTime.Today, TimeZoneInfo.Local.GetUtcOffset(DateTime.Today)).AddHours(hour).AddMinutes(minute);
}
