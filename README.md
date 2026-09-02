# OptionAnalytics

A dependency-free .NET library for European option pricing, greeks, and implied volatility —
Black-Scholes (1973), Merton (1973), Black-76 and Garman-Kohlhagen (1983), built as one model
rather than four.

It has no dependencies beyond the .NET base class library, no notion of a broker, an exchange or a
market, and no opinion about what you should do with the numbers. Reference it as a normal
package, or copy the files you need.

## The design: one formula, four models

Black-Scholes, Merton, Black-76 and Garman-Kohlhagen are not four formulas. They are one formula
evaluated at four values of a single **cost-of-carry** rate `b` — the drift of the underlying under
the pricing measure. That observation is Haug's, and it is why this library has one pricing
implementation and one set of greeks instead of four of each:

| carry | model | underlying |
|---|---|---|
| `b = r` | Black-Scholes (1973) | equity paying no dividend |
| `b = r − q` | Merton (1973) | equity or index paying a continuous dividend yield |
| `b = 0` | Black (1976) | option on a **future** — the futures price already embeds the carry |
| `b = r − rf` | Garman-Kohlhagen (1983) | currency — the foreign rate acts exactly like a dividend |

```csharp
using OptionAnalytics;

var nifty  = OptionModel.Merton(spot: 24_800, strike: 25_000, timeToExpiry: 7 / 365.0,
                                volatility: 0.12, riskFreeRate: 0.065, dividendYield: 0.012,
                                right: OptionRight.Call);

var future = OptionModel.Black76(futuresPrice: 24_850, strike: 25_000, timeToExpiry: 7 / 365.0,
                                 volatility: 0.12, riskFreeRate: 0.065, right: OptionRight.Call);

OptionGreeks? greeks = BlackScholesMerton.ComputeGreeks(nifty);
```

**This is the extensibility hinge.** Adding a fifth model later is a new factory method and nothing
else — no new pricing code, no new greeks, no new tests of the arithmetic. Everything downstream
already works, because it only ever sees `b`.

Two more choices exist for the same reason. `OptionInputs` is an immutable record rather than a
positional parameter list, so a parameter added in future is a new property with a default rather
than a breaking signature change — and `inputs with { Volatility = v }` is how the solver walks its
search and how the tests bump a price. And `ImpliedVolatility.Solve` returns a result record rather
than a bare `double`, so a better solver can be dropped in without touching a single caller.

## What's in it

**Prices and nine greeks**, all analytic closed forms: delta, gamma, vega, theta, two rhos, plus
vanna, volga and charm. `ComputeGreeks` evaluates the shared terms once rather than nine times.

**Implied volatility** by Newton's method inside a maintained bracket — quadratic convergence where
the function behaves, bisection's unconditional convergence where it doesn't.

**Put-call parity**, including deriving one leg's arbitrage-free value from the other's price.

**Expected price ranges** from a volatility — the practical question a volatility exists to answer.

**Risk-neutral probabilities** — of expiring in the money, of finishing beyond any level, and of
*touching* a level before expiry.

**Multi-leg payoffs**: profit at expiry, breakevens, bounds, and aggregate position greeks.

**A double-precision normal distribution**, because everything else bottoms out in it.

### There are two rhos, and picking the wrong one is a real error

Rho depends on whether the underlying's drift moves when the rate does — a property of the *model*,
not the contract. So this library computes both and names them, rather than silently choosing:

- **`RhoCarryTracksRate`** — the conventional equity and index rho. Use it with `BlackScholes` and
  `Merton`, where a change in `r` feeds through to the drift as well as the discount factor.
- **`RhoCarryFixed`** — equals `−T × Price` exactly, for every model. Use it with `Black76`, where
  the carry is structurally zero and the rate enters only through discounting.

### Greeks are raw derivatives until you say otherwise

Scaling is where implementations silently disagree. The raw derivatives are unambiguous — vega per
one *full unit* of volatility, theta per *year* — and almost nobody quotes them that way. Platforms
show vega per volatility **point** (a hundredth) and theta per **day** (a 365th or a 252nd). Two
correct libraries can differ by a factor of 100 or 365 and both be right.

`BlackScholesMerton` always returns the raw derivative. `GreekConventions` is the only place a
convention is applied, so a number's units are always traceable to an explicit choice:

```csharp
var shown = greeks!.Scaled();                                  // theta/day, vega/point, rho/point
var raw   = greeks.Scaled(GreekConventions.Raw);               // untouched derivatives
var desk  = greeks.Scaled(new GreekConventions(DaysPerYear: 252, RatePointsPerUnit: 10_000));
```

The default divides theta by 365, not 252, because an option decays over calendar time — a weekend
costs two days of extrinsic value though no session occurs.

### Where did the P&L actually come from

Attribution splits a mark-to-market change into the greek terms that produced it, and measures what
none of them explains.

```csharp
var yesterday = OptionModel.Merton(24_800, 24_800, 30 / 365.0, 0.13, 0.065, 0.012, OptionRight.Call);
var today     = yesterday with { Spot = 25_050, Volatility = 0.147, TimeToExpiry = 29 / 365.0 };

PnlAttributionResult? why = PnlAttribution.Explain(yesterday, today);
// why.DeltaPnl, .GammaPnl, .ThetaPnl, .VegaPnl, .VannaPnl, .VolgaPnl, .CharmPnl, .RatePnl, .CarryPnl
// why.Residual        what no term accounts for
// why.LargestTerm     ("DeltaPnl", 138.34)
```

It takes market **states**, not greeks, on purpose. Handing it an `OptionGreeks` would invite the
one mistake that silently ruins the arithmetic — passing values that have been through `Scaled()`,
where theta is per day and vega per volatility point. A per-day theta in a per-year expansion is
wrong by 365. Computing the greeks internally in raw units makes that unrepresentable.

**The diagnostic this exists for.** Take a short strangle described as harvesting time decay, on a
day the index moves 1.2%:

| | actual | theta | delta | gamma | largest |
|---|---|---|---|---|---|
| flat day | **+10.88** | +10.82 | 0.00 | 0.00 | `ThetaPnl` |
| 1.2% up day | **−49.84** | +10.82 | −28.82 | −32.76 | `GammaPnl` |

Every rupee of decay arrived exactly as advertised. It was irrelevant — the day was decided by short
gamma. A strategy whose P&L consistently lands in a term it isn't named after is a different bet
than its description, and that is mechanically visible here rather than a matter of interpretation.

`ExplainPosition` does the same for multi-leg positions, summing leg by leg with signs, so a short
leg contributes negative gamma automatically. Note this explains why a **mark moved**, not profit
since entry — premium paid is a constant and constants have no derivatives.

**Accuracy is measured, and the residual is not monotonic.** For a 30-day at-the-money call over one
day: 0.04% of the move unexplained at a 0.5% move, 0.6% at 2%, 5.9% at 5%, and **24% at 10%** —
where gamma itself moves over the gap. At the quiet end there is a floor of about 0.03 price units
per day, because the expansion is only first order in time, so a small move shows a large *share*
without the attribution being wrong. Read the residual in price units too.

### The carry is the one input you should not guess

Every price here is driven by the cost of carry `b`, and for an index it is worse than an
assumption: **the cash index is not tradeable, so there is no carry that makes it so.** Nobody buys
NIFTY. What trades is the future and the option chain, and those agree on a forward that a spot
level plus a dividend forecast will not reproduce.

The option market already states the answer in every call/put pair, because put-call parity is a
static arbitrage rather than a model result:

```csharp
var chain = new CallPutQuote[]          // one expiry, strikes you would actually trade
{
    new(Strike: 24_800, CallPrice: 423.91, PutPrice: 316.22),
    new(Strike: 24_900, CallPrice: 372.42, PutPrice: 364.20),
    new(Strike: 25_000, CallPrice: 325.20, PutPrice: 416.45),
};

ImpliedForwardResult? marketCarry = ImpliedForward.FromChain(spot: 24_800, chain, timeToExpiry: 30 / 365.0);

// Price and hedge every strike on that expiry off what the market is actually carrying at.
var contract25400 = OptionModel.BlackScholes(24_800, 25_400, 30 / 365.0, 0.13, 0.065, OptionRight.Call);
OptionInputs? onMarketCarry = marketCarry?.ApplyTo(contract25400);
```

`FromChain` needs **no interest rate** — parity is linear in the strike, so the call-put spread
regressed on the strike gives the discount factor as its slope and the discounted forward as its
intercept. The rate comes out as an *output*, which removes the last assumption from the chain. The
carry it returns absorbs the dividend yield, the borrow spread, the index basis and any funding skew
at once, none of them separately forecast.

It also checks itself. On clean simultaneous European quotes every pair sits on one line, so
`WorstParityResidual` is the diagnostic — a stale leg 12 points inside the bid/ask, invisible to
eyeballing, shows up as a residual above 5. **Below three strikes that residual is zero by
construction and means nothing**, which is precisely why the single-pair form is the weaker tool:

```csharp
// Only one strike quoted? This works, but supply a rate and it cannot detect its own failure.
var single = ImpliedForward.FromCallPut(spot: 24_800, chain[1], 30 / 365.0, riskFreeRate: 0.065);
```

If you must use one pair, pick the pair whose `Spread` is smallest in absolute value — not the
strike nearest spot. The error a wrong rate produces is `T·e^(rT)·(C−P)`, which vanishes at the
forward, and on a 30-day NIFTY carrying at 5.3% the forward sits **108 points above spot**, four
strikes away. Measured with the rate 200bp wrong: 0.014 at the near-forward strike, 0.178 at
spot-at-the-money, 4.78 at a strike 2,800 points down.

**European exercise only.** Parity breaks under early exercise, so this is right for NSE index
options and wrong for single-stock options, which are American. Nothing in the library can detect
that — American quotes yield a plausible, biased carry rather than a refusal.

### Implied volatility tells you when it can't be trusted

```csharp
var result = ImpliedVolatility.Solve(observedPrice: 142.5, inputs: nifty);
if (result is null)        { /* outside no-arbitrage bounds — a stale or crossed print */ }
else if (!result.Converged) { /* the solver did not reach tolerance — see below */ }
```

**`Converged` is the first thing to check, and it exists because the obvious checks lie.** A price
at or beyond the achievable range returns the clamped search bound with `PriceError` of exactly
*zero* and `Iterations` of *zero* — so the two fields you would naturally read both say the solve
was flawless when no solving happened at all. Exhausting the iteration cap is equally quiet.

A converged solve says the model reproduces the price. It does **not** say the answer is unique.
Where vega is vanishingly small — deep in the money, or a whisker from expiry — a wide range of
volatilities produces the same price to the last representable digit. Measured on a 40-point
in-the-money put a week from expiry: prices at 5% and 40% volatility are **identical to every
digit**, vega is `8e-71`, and `PriceError` is exactly zero. The solve is perfect; the volatility is
meaningless.

So `ImpliedVolatilityResult` reports **`VegaAtSolution`** and this library imposes no cutoff. Where
the threshold belongs depends on your price scale and tick size, which you know and it doesn't. A
rough rule: if a one-tick price change would move implied volatility more than you care about
(`tick / VegaAtSolution`), the answer isn't resolvable at that tick.

The two are different questions and both are reported: `Converged` asks whether the solver found the
volatility, `VegaAtSolution` asks whether that volatility means anything.

### From a volatility to a range you can act on

A volatility of 12% means nothing at the point of a decision. "NIFTY has a 68% chance of finishing
between X and Y by expiry" is the same fact in a usable form — it tells an option writer which
strikes are likely to expire worthless, and a position holder where a stop sits outside ordinary
noise rather than inside it.

```csharp
PriceRange? range = ExpectedMove.Range(spot: 24_800, annualisedVolatility: 0.13, days: 20, sigmas: 1);
double? weekly  = ExpectedMove.OverDays(0.13, days: 5);   // square-root-of-time rescaling
```

`Range` uses the lognormal form implied by the same process `BlackScholesMerton` prices against.
`SimpleRange` uses the arithmetic form taught in [Zerodha
Varsity](https://zerodha.com/varsity/chapter/volatility-applications/) — `price × (1 + mean ± SD)`
— which is easier to do in your head and agrees closely over an expiry cycle. They diverge as the
horizon lengthens: at 60% annualised volatility over a year, two sigma drives the arithmetic lower
bound **below zero**, which is not a price. Both are here, with tests pinning both the agreement
and the divergence.

One detail worth knowing if you are checking against Varsity's worked example (NIFTY 8462, 16 days,
upper 8818, lower 8214): the drift is **added to both bounds**, so the range is centred on the
drifted price. Subtracting it from the lower bound gives 8106, not the published figure.

### Delta is not the probability of expiring in the money

It is a good approximation and a widely taught one. The precise quantity is `N(d2)`; delta is
`N(d1)`, and the two differ by `σ√T` inside the normal. The gap is small for short-dated
near-the-money contracts and widens with time and volatility — which is exactly where a premium
seller is most likely to be leaning on it.

```csharp
var contract = OptionModel.Merton(spot: 24_800, strike: 25_000, timeToExpiry: 30 / 365.0,
                                  volatility: 0.13, riskFreeRate: 0.065, dividendYield: 0.012,
                                  right: OptionRight.Call);

double? itm     = OptionProbability.InTheMoneyAtExpiry(contract);         // N(d2), the real answer
double? gap     = OptionProbability.DeltaVersusProbabilityGap(contract);  // how wrong the rule is here
double? touches = OptionProbability.ProbabilityOfTouching(contract, barrier: 25_500);
```

`ProbabilityOfTouching` is roughly **twice** the probability of finishing beyond the same level, and
that factor of two is the point: a stop with a 15% chance of being closed through has closer to a
30% chance of being hit intraday first. Anyone sizing a stop from an expiry range alone is reading
the wrong number.

These are **risk-neutral** probabilities — the ones consistent with traded prices, not the
frequencies you would observe. Using them to argue a short put is a good bet is circular; they were
derived from that put's own price.

### Multi-leg positions

```csharp
var ironCondor = new StrategyLeg[]
{
    new(OptionRight.Put,  24_400, +1, 40),   // positive quantity = long
    new(OptionRight.Put,  24_700, -1, 95),   // negative = short
    new(OptionRight.Call, 25_100, -1, 90),
    new(OptionRight.Call, 25_400, +1, 38),
};

// One market state shared by every leg: same underlying, same expiry, same volatility.
// Strike and Right are taken from each leg, so whatever is passed here for them is ignored.
var market = OptionModel.Merton(spot: 24_800, strike: 25_000, timeToExpiry: 30 / 365.0,
                                volatility: 0.13, riskFreeRate: 0.065, dividendYield: 0.012,
                                right: OptionRight.Call);

StrategyProfile? shape = OptionStrategy.Profile(ironCondor);   // net cash, bounds, breakevens
OptionGreeks?    net   = OptionStrategy.NetGreeks(ironCondor, market);
```

There is deliberately **no catalogue of named structures** — no `IronCondor` factory. A vertical
spread is two legs; naming it adds a vocabulary to learn and a taxonomy to argue about, and the
names differ between desks anyway. Build the legs; the payoff does not care what anyone calls the
shape.

Because the payoff of European options is piecewise linear with kinks only at strikes, `Profile` is
**exact rather than sampled** — it evaluates the vertices and solves each segment for its root.
Note that only the *upside* can be unbounded: an underlying can rise without limit but cannot fall
below zero, so a short put's loss is always bounded, at a price of zero.

Payoffs are **at expiry**. A short strangle that will expire worthless can still be deeply
underwater a week beforehand, and that is what forces a margin call — use `NetGreeks` for the path
and `Profile` for the destination.

## How much of this has been verified

Correct arithmetic is the only thing this library claims, so here is how far that claim has been
checked. Three checks, deliberately independent of each other:

**1. Every greek against a finite-difference bump of `Price`**, over 1,440 contracts spanning
moneyness, maturity, volatility, negative and positive rates, and all four models. The closed form
and the price function share no code path, so agreement is evidence rather than tautology.

Getting the *tolerance* right mattered more than the test. A finite difference carries truncation
error (`~h²`, shrinking with `h`) and round-off error (`~ε·|P|/hⁿ`, **growing** as `h` shrinks).
Near a zero-valued greek the round-off term swamps the quantity outright — a deep OTM option's true
gamma can be `3e-11` while the second difference of its price is float noise at `1e-14`. An earlier
version of this test used a flat relative threshold and "failed" on exactly those points; it was
measuring the harness, not the library. Each greek is now judged against the finite difference's
own error bound.

**2. Put-call parity**, which follows from a static arbitrage rather than from Black-Scholes, so it
holds for any no-arbitrage model. It catches errors no single price reveals, because it constrains
the two sides against each other. Worst gap measured: **2.4e-16 of spot**.

**3. The Black-Scholes PDE identity** — `Theta + b·S·Delta + ½σ²S²·Gamma = r·V` — which ties four
separately computed quantities into one equation no-arbitrage forces to balance. Worst relative
residual: **1.7e-11**.

This is the check that earned its keep. It held to machine precision everywhere except at exactly
`σ = 0`, where it was out by **4.76** — because theta, the equity rho and charm all returned zero
there on the reasoning that no optionality means no sensitivity. That is true of the volatility
derivatives and false of the time and rate ones: the discount on the strike keeps unwinding whether
or not anything is uncertain. A discontinuity in a verified identity is the signature of a special
case that was guessed rather than derived. The identity is now asserted *across* that boundary
rather than near it.

Plus model equivalences (Black-76 on the forward reproduces Merton on the spot to `8e-14`; Merton
with zero dividend is *bit-identical* to Black-Scholes), implied-volatility round-trips judged
against the problem's own conditioning bound, and the degenerate cases below. **80 tests.**

### The normal distribution, and why it isn't the usual approximation

Most option-pricing code reaches for Abramowitz & Stegun 7.1.26, a five-term polynomial with a
maximum **absolute** error near 1.5e-7. That is ample for reading a delta off a chain and useless
here for two reasons: a deep out-of-the-money option can be worth 1e-9, and finite-difference
verification divides by a small bump, amplifying absolute error.

`Cdf(-10)` is `7.62e-24`. **A&S returns exactly zero.**

This library computes `erfc` from its convergent definitions instead — the Maclaurin series near
the origin (A&S 7.1.5), the continued fraction in the tails (A&S 7.1.14) by modified Lentz — so
accuracy is a property of the mathematics rather than of a fitted coefficient table, and can be
measured rather than asserted. Measured over 3,859 points across `[-25, 25]`: worst **relative**
error **2.9e-14** for `Cdf`, both tails included.

### Degenerate inputs are answered, not refused

At zero time to expiry or zero volatility the model has exact limits, not a hole: price collapses
to discounted **forward** intrinsic (not spot intrinsic — that would break parity), delta to a
step, gamma and vega to zero. An expiring option is the most common thing a real system asks about,
and an unguarded `log(0)` returns `NaN` there. Genuinely invalid inputs return `null`. Negative
rates are priced, not refused — they existed for a decade.

## What this library does not do

- **It does not price American options.** Everything here is European, exercisable only at expiry.
  That is correct for NSE index options, most cash-settled index products and FX; it is **not**
  correct for American-style single-stock options, where early exercise has value these formulas
  cannot see and will silently understate.
- **It assumes constant volatility.** It prices a single point on a smile, not the smile. Feed it a
  per-strike implied volatility and it will price each strike correctly; it has no view on how
  those relate.
- **It does not model dividends discretely.** A continuous yield is an approximation to a discrete
  dividend stream, and a poor one across a large ex-date.
- **It does not fetch data, know your instrument's lot size, or model transaction costs.**
  Spot, strike, time, volatility and rates are inputs you provide.
- **It has no opinion on which numbers are trustworthy.** It reports conditioning
  (`VegaAtSolution`) and lets you decide, rather than refusing to return a value that is
  mathematically correct but practically unusable.

## Sources

The formulas and conventions are taken from published references rather than from any single
implementation:

- Black & Scholes (1973); Merton (1973); Black (1976); Garman & Kohlhagen (1983) — the four models
- Haug, *The Complete Guide to Option Pricing Formulas* — the cost-of-carry generalisation
- [Greeks (finance)](https://en.wikipedia.org/wiki/Greeks_(finance)) — first-, second- and
  third-order greek formulas with continuous dividend yield
- Abramowitz & Stegun, *Handbook of Mathematical Functions* — 7.1.5, 7.1.14 and 7.1.26
- Jäckel, ["Let's Be Rational"](https://onlinelibrary.wiley.com/doi/abs/10.1002/wilm.10395),
  *Wilmott* (2015) — the state of the art in implied-volatility solving, and this library's
  documented upgrade path
- West, *Better Approximations to Cumulative Normal Functions* — on the accuracy of CDF
  approximations
- Acklam's rational approximation — the initial estimate for `InverseCdf`, refined against `Cdf`
- [Zerodha Varsity](https://zerodha.com/varsity/module/option-theory/), *Option Theory for
  Professional Traders* — the volatility-to-range applications, and the delta-as-probability rule
  of thumb this library quantifies rather than repeats
- Natenberg, *Option Volatility and Pricing*; Hull, *Options, Futures, and Other Derivatives* —
  standard references for the greeks and for the `N(d2)` versus delta distinction

## License

MIT — see `LICENSE`.
