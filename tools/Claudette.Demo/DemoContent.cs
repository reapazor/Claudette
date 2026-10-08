using System.Diagnostics;
using System.Text.Json.Nodes;
using Claudette.Core.Diffs;
using Claudette.Core.Sessions;
using Claudette.Core.Settings;

namespace Claudette.Demo;

/// <summary>
/// The demo's projects and tabs: three git repositories with Claude's changes in their working trees, and five
/// sessions over them, each with its transcript. The first tab, Apple Pay on checkout, is the one the screenshots show.
/// </summary>
internal static class DemoContent
{
    /// <summary>Writes the projects under <paramref name="projects"/> and the transcripts to <paramref name="transcripts"/>.</summary>
    /// <returns>The tabs, the one to show first.</returns>
    public static List<TabState> Write(string projects, string transcripts)
    {
        var storefront = Storefront(projects);
        var payments = Payments(projects);
        var orbit = Orbit(projects);

        var (applePay, firstWrite) = ApplePay(storefront);
        var flaky = Flaky(storefront);
        var (refunds, refundChanges) = Refunds(payments);
        var upgrade = Upgrade(payments);
        var pool = Pool(orbit);
        return
        [
            Tab("tab-applepay", storefront, applePay, "Apple Pay on checkout", reviewed: [(Path.Combine(storefront, "src", "checkout", "ApplePayButton.tsx"), firstWrite)]),
            Tab("tab-flaky", storefront, flaky, "Flaky cart total test", mark: TabMark.Question),
            Tab("tab-refunds", payments, refunds, "Idempotent refunds", mark: TabMark.Star, reviewed: refundChanges),
            Tab("tab-upgrade", payments, upgrade, "Upgrade to .NET 10", mark: TabMark.Flag, model: "Sonnet 5.5", effort: "low"),
            Tab("tab-pool", orbit, pool, "Pool particle emitters", mark: TabMark.Pause),
        ];

        TabState Tab(string id, string folder, DemoTranscript transcript, string name, TabMark? mark = null,
            (string Path, string Change)[]? reviewed = null, string model = "Opus 5.5", string effort = "high") => new()
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
            };
    }

    // ---- acme-storefront: Apple Pay on checkout --------------------------------------------------------------------

    private const string PaymentMethods = """
        import { CardForm } from './CardForm';
        import { PayPalButton } from './PayPalButton';
        import { useCheckout } from './useCheckout';

        export function PaymentMethods() {
          const { cart, methods, submit } = useCheckout();

          return (
            <section className="payment-methods">
              <h2>Payment</h2>
              {methods.includes('paypal') && <PayPalButton total={cart.total} onApprove={submit} />}
              <CardForm total={cart.total} onSubmit={submit} />
            </section>
          );
        }

        """;

    private const string ImportOld = "import { CardForm } from './CardForm';";

    private const string ImportNew = """
        import { ApplePayButton } from './ApplePayButton';
        import { CardForm } from './CardForm';
        """;

    private const string MethodsOld = """
              <h2>Payment</h2>
              {methods.includes('paypal') && <PayPalButton total={cart.total} onApprove={submit} />}
        """;

    private const string MethodsNew = """
              <h2>Payment</h2>
              <div className="express-checkout">
                <ApplePayButton total={cart.total} currency={cart.currency} onAuthorized={submit} />
                {methods.includes('paypal') && <PayPalButton total={cart.total} onApprove={submit} />}
              </div>
              <p className="divider">or pay by card</p>
        """;

    private const string ApplePayButton = """
        import { useEffect, useState } from 'react';
        import { api } from '../api';
        import type { Money } from '../money';

        type Props = {
          total: Money;
          currency: string;
          onAuthorized: (token: ApplePayJS.ApplePayPaymentToken) => Promise<void>;
        };

        /** Apple Pay, for browsers that can make payments with it. Renders nothing elsewhere. */
        export function ApplePayButton({ total, currency, onAuthorized }: Props) {
          const [supported, setSupported] = useState(false);

          useEffect(() => {
            setSupported(typeof window.ApplePaySession !== 'undefined' && ApplePaySession.canMakePayments());
          }, []);

          if (!supported) {
            return null;
          }

          const pay = () => {
            const session = new ApplePaySession(3, {
              countryCode: 'US',
              currencyCode: currency,
              supportedNetworks: ['visa', 'masterCard', 'amex'],
              merchantCapabilities: ['supports3DS'],
              total: { label: 'Acme', amount: total.toFixed(2) },
            });
            session.onvalidatemerchant = async ({ validationURL }) => {
              const res = await api.post('/payments/apple-pay/session', { validationURL });
              session.completeMerchantValidation(await res.json());
            };
            session.onpaymentauthorized = async ({ payment }) => {
              try {
                await onAuthorized(payment.token);
                session.completePayment(ApplePaySession.STATUS_SUCCESS);
              } catch {
                session.completePayment(ApplePaySession.STATUS_FAILURE);
              }
            };
            session.begin();
          };

          return <apple-pay-button buttonstyle="black" type="buy" locale="en-US" onClick={pay} />;
        }

        """;

    private const string ApplePayTest = """
        import { render, screen } from '@testing-library/react';
        import { ApplePayButton } from './ApplePayButton';
        import { money } from '../money';

        const total = money(42.5);

        describe('ApplePayButton', () => {
          afterEach(() => delete (window as any).ApplePaySession);

          it('renders nothing when Apple Pay is unavailable', () => {
            render(<ApplePayButton total={total} currency="USD" onAuthorized={jest.fn()} />);
            expect(screen.queryByRole('button')).toBeNull();
          });

          it('renders the button when the device can make payments', () => {
            (window as any).ApplePaySession = { canMakePayments: () => true };
            render(<ApplePayButton total={total} currency="USD" onAuthorized={jest.fn()} />);
            expect(document.querySelector('apple-pay-button')).not.toBeNull();
          });
        });

        """;

    private const string CartTest = """
        it('totals the cart', async () => {
          const cart = await loadCart();
          expect(cart.total).toBe(59.97);
        });

        """;

    private const string LoadCartOld = "const cart = await loadCart();";

    private const string LoadCartNew = "const cart = await loadCart({ prices: fixedPrices });";

    private static string Storefront(string projects) => Project(projects, "acme-storefront", "feature/apple-pay",
        new()
        {
            ["package.json"] = "{\n  \"name\": \"acme-storefront\",\n  \"private\": true,\n  \"scripts\": { \"test\": \"jest\" }\n}\n",
            ["src/checkout/PaymentMethods.tsx"] = PaymentMethods,
            ["src/checkout/useCheckout.ts"] = "import { useCart } from '../cart';\n\nexport function useCheckout() {\n  const cart = useCart();\n  return { cart, methods: ['card', 'paypal'], submit: cart.checkout };\n}\n",
            ["src/checkout/CardForm.tsx"] = "export function CardForm() {\n  return null;\n}\n",
            ["src/cart/cart.test.ts"] = CartTest,
        },
        new()
        {
            ["src/checkout/PaymentMethods.tsx"] = PaymentMethods.Replace(ImportOld, ImportNew).Replace(MethodsOld, MethodsNew),
            ["src/checkout/ApplePayButton.tsx"] = ApplePayButton,
            ["src/checkout/ApplePayButton.test.tsx"] = ApplePayTest,
            ["src/cart/cart.test.ts"] = CartTest.Replace(LoadCartOld, LoadCartNew),
        });

    private static (DemoTranscript Transcript, string FirstWrite) ApplePay(string root)
    {
        var checkout = Path.Combine(root, "src", "checkout");
        var t = new DemoTranscript("5f6b2c1e-7d0a-4c55-9a51-2b8e6f0c4d11", root, "feature/apple-pay", Today(9, 12))
            .Prompt("Add Apple Pay to the checkout page. Only offer it when the browser can actually pay with it (`ApplePaySession.canMakePayments()`), and keep the card form as the fallback. Add tests.")
            .Thinking("Checkout picks its payment methods somewhere around PaymentMethods. Find where the list is built, then add a button that feature-detects Apple Pay before rendering.")
            .Text("I'll start by finding where checkout decides which payment methods to show.");
        t.Tool("Grep", new JsonObject { ["pattern"] = "methods", ["path"] = "src/checkout", ["output_mode"] = "files_with_matches" },
            "Found 2 files\nsrc/checkout/PaymentMethods.tsx\nsrc/checkout/useCheckout.ts",
            new JsonObject { ["mode"] = "files_with_matches", ["filenames"] = new JsonArray("src/checkout/PaymentMethods.tsx", "src/checkout/useCheckout.ts"), ["numFiles"] = 2 });
        t.Read(Path.Combine(checkout, "PaymentMethods.tsx"), PaymentMethods)
            .Text("`PaymentMethods` renders PayPal and the card form from `useCheckout`. I'll add an `ApplePayButton` that renders only when the device can pay, and put it above the others.");
        var firstWrite = t.Create(Path.Combine(checkout, "ApplePayButton.tsx"), ApplePayButton);
        t.Edit(Path.Combine(checkout, "PaymentMethods.tsx"), PaymentMethods, ImportOld, ImportNew);
        t.Edit(Path.Combine(checkout, "PaymentMethods.tsx"), PaymentMethods.Replace(ImportOld, ImportNew), MethodsOld, MethodsNew);
        t.Create(Path.Combine(checkout, "ApplePayButton.test.tsx"), ApplePayTest);
        t.Bash("npm test -- src/checkout", "Run the checkout tests", """
             PASS  src/checkout/ApplePayButton.test.tsx
             PASS  src/checkout/PaymentMethods.test.tsx

            Test Suites: 2 passed, 2 total
            Tests:       9 passed, 9 total
            Snapshots:   0 total
            Time:        2.81 s
            """)
            .Text("""
                Apple Pay is on checkout, behind feature detection.

                **What changed**
                - `ApplePayButton.tsx` shows Apple's button and starts an `ApplePaySession` for the cart total. It renders nothing unless `ApplePaySession.canMakePayments()` is true, so other browsers never see it.
                - `PaymentMethods.tsx` groups it with PayPal as express checkout, above the card form, which stays as the fallback.
                - `ApplePayButton.test.tsx` covers both cases.

                Merchant validation goes through your existing payments route:

                ```ts
                session.onvalidatemerchant = async ({ validationURL }) => {
                  const res = await api.post('/payments/apple-pay/session', { validationURL });
                  session.completeMerchantValidation(await res.json());
                };
                ```

                All 9 checkout tests pass. Before it works in production you'll need Apple's domain association file in `public/.well-known/`.
                """, last: true)
            .Title("Apple Pay on checkout");
        return (t, firstWrite);
    }

    private static DemoTranscript Flaky(string root)
    {
        var t = new DemoTranscript("8a1d4e77-3b2c-4f10-8e6d-91c0b7a5e222", root, "feature/apple-pay", Today(8, 40))
            .Prompt("The cart total test fails about one run in ten on CI. Can you find out why?")
            .Text("It reads live prices, which a nightly job updates. I'll pin them for the test.");
        t.Edit(Path.Combine(root, "src", "cart", "cart.test.ts"), CartTest, LoadCartOld, LoadCartNew);
        return t.Text("Fixed: the test now uses fixed prices. Should the nightly job stop writing to the shared test database too?", last: true);
    }

    // ---- payments-api: idempotent refunds, every file reviewed -----------------------------------------------------

    private const string RefundEndpoint = """
        namespace Payments.Api.Refunds;

        public static class RefundEndpoint
        {
            public static async Task<IResult> Handle(RefundRequest request, IRefunds refunds)
            {
                var refund = await refunds.CreateAsync(request.PaymentId, request.Amount);
                return Results.Ok(refund);
            }
        }

        """;

    private const string HandleOld = """
            public static async Task<IResult> Handle(RefundRequest request, IRefunds refunds)
            {
                var refund = await refunds.CreateAsync(request.PaymentId, request.Amount);
        """;

    private const string HandleNew = """
            public static async Task<IResult> Handle(
                RefundRequest request, [FromHeader(Name = "Idempotency-Key")] string key, IRefunds refunds, IIdempotencyStore store)
            {
                var refund = await store.GetOrCreateAsync(key, () => refunds.CreateAsync(request.PaymentId, request.Amount));
        """;

    private const string IdempotencyStore = """
        namespace Payments.Api.Refunds;

        /// <summary>Remembers each request's result by its Idempotency-Key, so a retried refund isn't paid twice.</summary>
        public interface IIdempotencyStore
        {
            Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> create);
        }

        """;

    private const string ApiProject = """
        <Project Sdk="Microsoft.NET.Sdk.Web">
          <PropertyGroup>
            <TargetFramework>net9.0</TargetFramework>
          </PropertyGroup>
        </Project>

        """;

    private static string Payments(string projects) => Project(projects, "payments-api", "fix/refund-idempotency",
        new()
        {
            ["Payments.slnx"] = "<Solution />\n",
            ["src/Payments.Api/Payments.Api.csproj"] = ApiProject,
            ["src/Payments.Api/Refunds/RefundEndpoint.cs"] = RefundEndpoint,
        },
        new()
        {
            ["src/Payments.Api/Payments.Api.csproj"] = ApiProject.Replace("net9.0", "net10.0"),
            ["src/Payments.Api/Refunds/RefundEndpoint.cs"] = RefundEndpoint.Replace(HandleOld, HandleNew),
            ["src/Payments.Api/Refunds/IIdempotencyStore.cs"] = IdempotencyStore,
        });

    private static (DemoTranscript Transcript, (string Path, string Change)[] Reviewed) Refunds(string root)
    {
        var refunds = Path.Combine(root, "src", "Payments.Api", "Refunds");
        var t = new DemoTranscript("c47e9a02-51d8-4b6e-a3f2-0e9d8c7b6a33", root, "fix/refund-idempotency", Today(7, 55))
            .Prompt("A retried refund request paid out twice yesterday. Make refunds idempotent with an Idempotency-Key header.")
            .Text("I'll key each refund by the header and return the first result on a retry.");
        var edit = t.Edit(Path.Combine(refunds, "RefundEndpoint.cs"), RefundEndpoint, HandleOld, HandleNew);
        var write = t.Create(Path.Combine(refunds, "IIdempotencyStore.cs"), IdempotencyStore);
        t.Text("Refunds are idempotent now: a retry with the same key gets the first refund back.", last: true);
        return (t, [(Path.Combine(refunds, "RefundEndpoint.cs"), edit), (Path.Combine(refunds, "IIdempotencyStore.cs"), write)]);
    }

    private static DemoTranscript Upgrade(string root)
    {
        var t = new DemoTranscript("e2b5d8f1-9c3a-4d7e-b6a0-4f1e2d3c5b44", root, "fix/refund-idempotency", Today(7, 20))
            .Prompt("Move the API to .NET 10.")
            .Text("Updating the target framework first.");
        t.Edit(Path.Combine(root, "src", "Payments.Api", "Payments.Api.csproj"), ApiProject, "net9.0", "net10.0");
        return t.Text("The project targets `net10.0`. Next: the test projects and the Docker base image.", last: true);
    }

    // ---- orbit-game ------------------------------------------------------------------------------------------------

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
