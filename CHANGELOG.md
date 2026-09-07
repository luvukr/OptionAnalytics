# Changelog

## 1.6.0

Adds `TradingClock`, `TradingSession` and `TimeRemaining`: a time to expiry that survives expiry
day. Also reverses a claim this release was originally written to make, because the measurement
contradicted it.

### The defect being removed is upstream of the library

`(expiry - now).TotalDays / 365` is a whole-day quantity answering an intraday question, so on
expiry day it is zero from the opening bell. Handed a zero, `BlackScholesMerton` correctly reports an
expired contract -- gamma and vega zero, no implied volatility to solve -- at 11:30 in the morning,
when gamma is the largest number on the book. The pricer is not at fault and cannot fix it. The
arithmetic that produced the zero is, and that is what this replaces.

Measured at 11:30 on expiry day, 24,800 at-the-money call, 240 trading minutes left:

| | price | gamma | vega | IV solvable |
|---|---|---|---|---|
| naive daily `T` | **0.000** | 0 | 0 | **no** |
| computed from instants | **27.79** | 5.79e-3 | 211.4 | yes |

A test also pins gamma rising monotonically through the session and exceeding 0.02 by 15:15, which
is the 0-DTE gamma behaviour the naive figure erases entirely.

### No exchange calendar, deliberately

Session hours change by circular and holidays change every year. A hardcoded table that has gone
stale is worse than no table, because it still looks authoritative -- the same reasoning that keeps a
lot-size table out of this package. You supply the `TradingSession` and the count of full trading
days between the dates. `TradingSession.IndiaEquity` (09:15-15:30, 375 minutes) is a convenience
value you can replace, not a commitment; if the exchange moves its hours, that constant is wrong and
yours is right. `tradingDaysPerYear` and `calendarDaysPerYear` are parameters for the same reason.

### The two clocks: what this release was going to say, and what the measurement said

Variance accrues only while the market is open; interest accrues every calendar day. Two clocks
genuinely exist, and the intended headline was that trading time is the better one to price on.

It is not, with this library. `OptionInputs` carries a single `TimeToExpiry` which the pricer uses
for the diffusion AND for `r*T` and `b*T`. A trading-time value gets the variance right and then
charges interest over a span that is not the calendar period. Measured on a three-week at-the-money
call: **1.24% mispricing**, which is error rather than refinement.

That the gap is entirely discounting is proven rather than argued -- rescaling the rate and carry by
the ratio of the two clocks reproduces the calendar-clock price to **exactly zero difference**, and
gamma agrees to 0.08% either way. Both facts are asserted by tests.

So `CalendarYears` is the recommendation for pricing, and `TradingYears` carries a
do-not-pass-this-to-OptionInputs warning at the point of use. The documentation was rewritten around
the measurement after it was taken, including one figure originally quoted from the trading clock
while the text recommended the calendar one.

`VolatilityInTradingTime` converts a calendar-annualised volatility -- which is what market quotes
and India VIX are -- into the trading-time-annualised one that belongs with `TradingYears`,
preserving total variance to eight significant figures. It is there so that anyone with a reason to
work in trading time is not silently pairing it with a volatility annualised on the other clock, not
as a way to get different prices.

### What the trading clock is genuinely for

Reading the time rather than pricing it. `TradingMinutesRemaining` counts 375 at the open to 0 at the
bell. `NoTradingTimeLeft` distinguishes a state calendar time cannot express: after Friday's close on
a Monday-morning expiry there is calendar time remaining but no trading time, so no further variance
can accrue even though the discount factor still has a weekend to unwind.

### Refusals

Expiry before now, a session closing before it opens, a negative trading-day count, and non-positive
or non-finite day-count divisors all return `null`. Expiry exactly at `now` is valid and returns zero
on both clocks -- the one moment a zero is the truth rather than an artefact.

### Tests

257, up from 235. Minute-by-minute countdown through expiry day, instants before the open and after
the close, a holiday today, the multi-day decomposition, the weekend case, variance preservation,
the 1.24% discounting error and its proof, convention parameters, and a custom four-hour session.

## 1.5.0

Adds `ImpliedVolatility.SolveBidAsk` and `BidAskVolatility`: a quoted option's volatility seen from
both sides of the spread. Also fixes a defect in that new wrapper, found by its own test.

### Why solving the mid is not enough

Feeds stream bids and asks. Solving the mid is the obvious move and it fails often: on an illiquid
strike the mid is frequently not a price any volatility produces, and `Solve` correctly refuses it.
Handed a whole chain, that refusal is common rather than exceptional -- measured on a 1,000-strike
NIFTY chain a week from expiry, 13% of strikes had no resolvable volatility at all. One strike in
eight is not an edge case, and a caller who only solves the mid never learns which eighth.

### The width of the bracket is the answer, not the price spread

One quote 0.20 wide, across a 7-day chain:

| strike | fair | vega | IV width | vs ATM |
|---|---|---|---|---|
| 24,800 | 190.86 | 1366.89 | **0.015pt** | 1.0x |
| 25,500 | 13.57 | 457.72 | 0.044pt | 3.0x |
| 25,800 | 2.64 | 141.64 | 0.141pt | 9.7x |
| 26,000 | 0.74 | 51.81 | **0.388pt** | 26.5x |
| 26,200 | 0.18 | 15.92 | cannot carry a 0.20 quote at all | |

The same twenty paise buys twenty-six times the volatility uncertainty, because vega has collapsed.
A price spread is denominated in the wrong unit for the decision.

The width is exactly `(ask - bid) / vega`, verified to a thousandth across four strikes -- which
makes the bracket the measured form of the tick-resolvability rule already documented under
`VegaAtSolution`, rather than a second opinion about it.

### Sides fail independently

Each side is a full `ImpliedVolatilityResult`, so `Converged` and `VegaAtSolution` survive per side
and no information is lost to a flattened return type. A zero bid -- routine in the wings -- behaves
two different ways and both are reported:

- out of the money, the bid clamps to the volatility floor with `Converged == false`
- in the money, the bid is below intrinsic and comes back `null`, since there is nothing to clamp to

`IsResolvable` catches both, which is why it tests `Converged` rather than merely non-null. Nothing
substitutes a floor, a nearest bound, or the other side's answer for a side that cannot be solved.

`Mid` is SOLVED at the mid price rather than averaged from the two sides. Price is non-linear in
volatility so the two genuinely differ, but the gap is small -- 0.002 volatility points on an
at-the-money quote eight points wide, rising to 0.145 on a wing. The reason to solve it is that it is
the only one of the two that reprices the mid, not that averaging is inaccurate. An earlier draft of
this documentation claimed the two "differ materially"; the measurement says otherwise and the
wording was corrected.

### Fixed: an invalid solver setting was reported as a market condition

`SolveBidAsk(..., tolerance: 0)` returned a NON-null result with all three sides `null`. `Solve`
refuses a non-positive tolerance, so every side refused individually -- and the result was
indistinguishable from a strike the market genuinely does not price. A programmer error arrived
dressed as a market observation, which is the worst available failure for a class whose entire job is
to say which strikes are real.

Solver settings are configuration, not market data, so an unusable one is now a single refusal up
front. The return contract is stated explicitly: all three sides `null` INSIDE a result means the
market does not price this strike, a `null` result means the call was not answerable, and the two must
not be confusable.

Found by a test written to check that the parameters were honoured, which is the only reason it was
found before publication.

### Refusals

A crossed quote (`bid > ask`) is refused rather than reported: the result's whole meaning is a
bracket, and a bid above an ask brackets nothing -- it means one side is stale or the feed is wrong,
a data problem rather than a market condition. A locked quote (`bid == ask`) is fine and returns zero
width. Negative or non-finite prices, an expired contract, and an unusable contract are all refused.

### Tests

235, up from 211. Including the cross-chain width comparison above, the `(ask - bid) / vega`
identity, bracket monotonicity at every strike, per-side repricing, a two-volatility round trip, both
zero-bid behaviours, the solved-versus-averaged mid, and a 101-strike sweep asserting that a real
chain contains both resolvable and unresolvable strikes.

One test had to be rewritten rather than the code: it built quotes as a percentage of fair value,
which puts the bid below intrinsic on a deep in-the-money call. The bid side correctly had no
volatility. Quotes in that test are now built from volatilities so both sides are achievable by
construction.

## 1.4.0

Adds `PnlAttribution`: a mark-to-market change split into the greek terms that produced it, plus the
part none of them explains.

Pure arithmetic over greeks the library already computes and verifies -- no new mathematics, one new
identity, and a deliberate API choice that makes the obvious mistake unrepresentable.

### The expansion

`dV ~ Delta*dS + 0.5*Gamma*dS^2 + Theta*dt + Vega*dSigma + Vanna*dS*dSigma + 0.5*Volga*dSigma^2
+ Charm*dS*dt + dV/dr*dr + dV/db*db`

`Actual` is obtained by repricing at both states rather than by summing terms, so the number the
attribution has to explain is exact. `Explained` and `Residual` are computed properties, not stored
fields, so they cannot drift out of agreement with the terms they summarise.

### It takes market states, not greeks

Accepting an `OptionGreeks` would invite the one error that silently ruins the arithmetic: passing
values that have been through `Scaled()`. Those are display quantities -- theta per day, vega per
volatility point -- and the expansion needs true derivatives. A per-day theta in a per-year
expansion is wrong by a factor of 365. Greeks are therefore computed internally in raw units from
the states supplied, and there is no way to pass the wrong convention. A test pins the distinction.

### A new identity, verified three ways

The expansion needs `dV/db`, the sensitivity to the carry with the rate held fixed, which the
library does not expose. The two rhos determine it: `RhoCarryTracksRate` is `dV/dr` WITH the carry
following, which is `dV/dr + dV/db`, and `RhoCarryFixed` is `dV/dr` alone. Their difference is
`dV/db`, and it reduces to `T * S * Delta`.

Checked against that closed form and against a finite-difference bump of the price, across
in-, at- and out-of-the-money, calls and puts, 7 and 90 days. All three agree to the limit of the
finite difference.

This also means rate and carry are attributed as genuinely independent coordinates. In this
parameterisation `r` and `b` are separate inputs, so a dividend revision and a rate move produce
separate terms rather than being conflated.

### The diagnostic it exists for

A short strangle described as harvesting time decay, on a day the index moves 1.2%:

| | actual | theta | delta | gamma | largest term |
|---|---|---|---|---|---|
| flat day | **+10.88** | +10.82 | 0.00 | 0.00 | `ThetaPnl` |
| 1.2% up day | **-49.84** | +10.82 | -28.82 | -32.76 | `GammaPnl` |

Every rupee of decay arrived exactly as advertised, and was swamped. The narrative is true and
irrelevant: the day was decided by short gamma. A strategy whose P&L lands consistently in a term it
is not named after is a different bet from its description, and `LargestTerm` makes that mechanical
rather than interpretive.

### Measured accuracy, including the end everyone misreads

For a 30-day at-the-money call over one day, residual as a share of the actual change:

| move | residual share |
|---|---|
| 0.05% | 4.4% |
| 0.5% | **0.04%** |
| 2% | 0.6% |
| 5% | 5.9% |
| 10% | **24%** |

The high end is the expansion failing -- gamma moves over a gap that size, and the error grows
faster than the cube of the move. A test asserts the 10% case is badly explained, because a library
that quietly returned a confident-looking breakdown of a gap day would be worse than one that says
so.

The low end is a different thing and easy to misread. The expansion is only FIRST order in time, so
the missing second time derivative leaves a residual floor of about 0.03 price units per day that
does not shrink as the market goes quiet. A test pins that floor and pins that shrinking the spot
move does not remove it. On a still day the share looks poor because the denominator is small, not
because the attribution is.

`ResidualShare` returns `null` rather than a large number when nothing happened, since a ratio
against zero is meaningless.

### Refusals

Time running backwards returns `null` -- almost always swapped arguments, which would otherwise
produce a plausible breakdown with every sign inverted. So do two states describing different
contracts, invalid states, and an unusable position.

### Tests

211, up from 187. The wiring is tested by moving one variable at a time and asserting exactly which
terms fire and which are still zero, since the real risk in this class is not the mathematics but
pairing the wrong greek with the wrong move -- vega against the spot change, charm against the
volatility change. Every such transposition is caught. Plus the `dV/db` identity, the per-year theta
convention, position sums against per-leg sums, the residual scaling, the time-curvature floor, and
the strangle diagnosis above.

## 1.3.0

Adds `ImpliedForward`: the forward price and cost of carry the option market is actually quoting,
extracted from call/put pairs instead of assumed.

### Why the carry was the weakest input in the library

Every price here is driven by `CostOfCarry`, and until now that number had to come from outside --
a dividend-yield forecast, a borrow assumption, a rate off a feed. For an index that is worse than
an assumption. **The cash index is not a tradeable instrument, so there is no carry that makes it
one.** Nobody buys NIFTY. What trades is the future and the option chain, and those two agree with
each other on a forward that a spot level plus a dividend guess does not reproduce.

Put-call parity is a static arbitrage, not a Black-Scholes result, so `C - P = exp(-rT)(F - K)` holds
whatever anyone believes about volatility -- including across a skew. Rearranged, it hands over the
forward.

### `FromChain` needs no interest rate at all

Parity is linear in the strike: `C(K) - P(K) = exp(-rT)F - exp(-rT)K`. Regressing the call-put
spread on the strike therefore recovers **both** unknowns -- the discount factor is minus the slope,
the discounted forward is the intercept -- so the rate is an output rather than an input. That
removes the last external assumption from pricing an index chain.

The carry that comes back absorbs the dividend yield, the borrow or repo spread, the index basis and
any funding skew simultaneously, none of them separately estimated. Their total is directly
observable; individually they are guesses.

### It checks itself, and the single-pair form cannot

On clean simultaneous European quotes every pair sits on one line, so `WorstParityResidual` is a real
diagnostic. Measured: a chain with one put quoted 12 points stale -- well inside a plausible
bid/ask, invisible to eyeballing -- moves the residual from below `1e-8` to above 5, while genuinely
contaminating the carry.

**Below three strikes that residual is zero by construction**, because a straight line through two
points fits them exactly however bad the quotes are. A test pins that with deliberately absurd
quotes, so the limitation cannot be mistaken for a clean fit. `FromCallPut` -- one pair plus an
assumed rate -- is provided for chains where only one strike is quoted, and its documentation says
plainly that it cannot detect its own failure.

### Which strike to use, and a correction to the obvious answer

"Use the at-the-money strike" is wrong if at-the-money means spot. The error a wrong rate produces is
`T*exp(rT)*(C-P)`, which vanishes at the FORWARD, and on a 30-day NIFTY carrying at 5.3% the forward
sits 108 points above spot -- four strikes away on a 100-point grid. Measured with the supplied rate
200bp too high:

| strike | C - P | forward error |
|---|---|---|
| 24,900 (nearest forward) | 8.22 | **0.014** |
| 24,800 (spot at-the-money) | 107.69 | 0.178 |
| 22,000 | 2,892.77 | 4.785 |

Since the forward is the unknown, the usable rule is the observable one: pick the pair whose spread
is smallest in absolute value. A test asserts the closed-form sensitivity predicts the measured error
at every strike to within 1%.

### Refusals rather than clamps

Non-positive implied forward, a call-put spread that RISES with strike (no arbitrage-free market
does that), fewer than two distinct strikes, zero time to expiry -- each returns `null`. With no time
left the forward is the spot and the carry is unidentifiable, so any figure returned would be
invented. Invalid quotes inside a chain are skipped rather than poisoning the fit, provided two
distinct strikes survive.

### European exercise only

Parity does not hold under early exercise. This is correct for NSE index options and wrong for
single-stock options, which are American -- and nothing here can detect the difference, so American
quotes produce a plausible biased carry rather than a refusal. Documented at the class level.

### Tests

187, up from 159. Round-trips through all four model parameterisations plus negative rates and a
carry above the rate; a chain built with a **different implied volatility at every strike**, since
parity is model-free and an extraction that only worked on a flat surface would be quietly assuming
something it has no right to; the stale-leg detection; the sensitivity closed form; and the
motivating case, where guessing the dividend yield at zero misprices a 600-point out-of-the-money
call by more than five points while the extracted carry reprices it exactly.

## 1.2.0

Fixes four defects found by an independent review of 1.1.0, plus two the review's rejected findings
led to. Nothing was published before this version, so nothing downstream breaks.

### Fixed: three greeks were zero at zero volatility when they are not

`Theta`, `RhoCarryTracksRate` and `Charm` returned `0.0` for any contract with zero volatility or
zero time, on the reasoning that no optionality means no sensitivity. That is right for the
VOLATILITY derivatives -- gamma, vega, vanna and volga genuinely do vanish -- and wrong for the
TIME and RATE ones. With no optionality the price is still `S*exp((b-r)T) - K*exp(-rT)`: the
discount on the strike keeps unwinding, and when the carry differs from the rate the forward keeps
drifting.

For a 110/100 one-year call at zero volatility and a 5% rate:

| | was | now |
|---|---|---|
| `Theta` | `0` | `-4.7561471225035703` |
| `RhoCarryTracksRate` | `0` | `95.122942450071406` |

Both agree with the analytic derivative of the discounted forward intrinsic to the last digit, and
with the Black-Scholes PDE identity independently. The identity is what exposed it: it held to
machine precision at `sigma = 1e-8` and was violated by **4.76** at exactly `sigma = 0`. A
discontinuity in a verified identity is the signature of a special case that was guessed rather
than derived.

`RhoCarryFixed` was always correct here, because it delegates to `Price` and never consulted the
degenerate branch -- two rhos for the same contract disagreeing is what settled that this was a bug
and not a convention.

`Charm` had the same hole whenever `b != r`; the review found the other two and missed this one.

Exactly at the money every degenerate greek still returns zero. The payoff has a kink there, the
left and right derivatives differ, and zero is the only answer that does not privilege one side.

### Fixed: an undefined `OptionRight` produced a confident, fabricated price

`OptionInputs.IsValid` did not check `Right` against its two defined members. An enum in .NET is a
suggestion, not a constraint, and since neither member is zero, `default` and a JSON payload with
the field missing both yield an undefined value. `Sign` is `(int)Right`, which multiplied straight
through the formula:

```
new OptionInputs(100, 100, 1.0, 0.20, 0.065, 0.065, (OptionRight)99)
  IsValid -> true      Price -> 482.82869744293077
```

A 100-strike at-the-money call priced at 482.83 -- not a zero, not a `NaN`, but a plausible number
24 times too large. `(OptionRight)0` was quieter and no better: price and delta zero, but
`Gamma = 0.0187620` and an in-the-money probability of `0.5`, both real-looking. `IsValid` now
rejects both, so every entry point refuses instead.

### Fixed: `ProbabilityOfTouching` could return `NaN`

The reflection-principle formula forms `exp(2*mu*a/sigma^2) * N(z)`, a product that is at most 1 by
construction. Evaluated directly it overflows: at a low volatility with a strong drift the exponent
passes 709, so `exp` returns `+Infinity` while `N(z)` has already underflowed to exactly zero, and
`Infinity * 0` is `NaN` -- which survived the final `Math.Clamp` and would propagate silently
through everything downstream.

The product is now formed in log space, with an asymptotic tail expansion for `log N(z)` where the
CDF itself underflows. The case that produced `NaN` now returns `4.3257e-285`.

Reachability is low -- it needs the exponent above 709, and realistic parameters land between 1.6
and 8.3 -- but `NaN` is the one failure mode that cannot be noticed downstream, and returning it
for valid input broke the library's own contract. A volatility small enough that its square
underflows is also now answered deterministically instead of dividing by zero.

### Fixed: `ProbabilityBelow` turned an atom of probability into certainty

Derived as `1 - ProbabilityAbove`, which is right wherever the distribution is continuous and wrong
at the one place it is not. With no time or volatility left the terminal price is a single known
point; if that point IS the level, the underlying finishes neither above nor below it.
`ProbabilityBelow` reported `1` for finishing below a level the price sits exactly on, while
`InTheMoneyAtExpiry` correctly reported `0` for the corresponding put. Strict inequalities on both
sides now keep the two consistent. Where the distribution is continuous they still sum to 1 exactly.

This came out of cross-checking a review finding that was itself wrong -- see below.

### Fixed: documented delta range was too narrow

Delta is `exp((b-r)T) * N(d1)`, so its bound is the carry discount factor, not 1. Whenever the
carry exceeds the rate -- a negative dividend yield, a foreign rate below the domestic one -- a
deep in-the-money delta legitimately exceeds 1.0; a Merton call with a -5% yield over two years
reaches `1.1040538`. The docs said `[-1, 1]` in two places. Only under Black-Scholes proper, where
`b = r`, is that the range.

### Changed: `ImpliedVolatilityResult` gained `Converged`

Three different failure modes were each signalled by a different field and none was a plain
verdict. The trap is the bound clamp: a price at or beyond the achievable range returns the clamped
bound with `PriceError` of exactly **zero** and `Iterations` of **zero**, so the two fields a
caller is most likely to read say the solve was flawless when no solving happened at all.
Exhausting the iteration cap was equally quiet.

`Converged` is true only if the requested tolerance was actually reached. `VegaAtSolution` remains
the separate question of whether a converged answer is *unique* -- the deep-in-the-money case now
fails both checks rather than only the one a caller might not think to make.

This adds a member to a positional record, which would be a breaking change after publication. It
is free now and was not going to get cheaper.

### Changed: breakeven de-duplication scales with the price

Adjacent segments meeting at a root each report it, so near-duplicates are merged. The threshold
was a fixed `1e-9`, which is tighter than the rounding noise in a root solved near NIFTY's 25,000
and looser than the gap between distinct roots on a sub-microcent instrument. Now `strike * 1e-9`,
floored at `1e-12`. No misbehaviour was demonstrated at either extreme -- this is robustness, not a
fix.

### Two review findings rejected, and why

**"An at-the-money option at expiry should report a 50% chance of finishing in the money."** The
argument was that `P(call ITM) + P(put ITM)` must equal 1. That axiom requires `P(S_T == K) = 0`,
which is exactly what is false here: with no time left the terminal price IS the strike and all the
mass sits on that atom. Neither leg finishes beyond the strike, so `0` for both is correct.
Returning `0.5` would claim a coin flip on an already-settled contract. `N(d2)` does tend to 0.5 as
`T -> 0+`, but the value AT `T = 0` is a fact, not a forecast.

**"`OptionGreeks.Scaled` should return null rather than throw."** `GreekConventions` is developer
configuration, not market data. `DaysPerYear: 0` is a coding error, and a misconfigured constant
should fail loudly on the first call rather than silently null out every greek in a pricing loop.
The exception is documented on both `Scaled` and `Validate`, and `Validate` is public for anyone
binding conventions from configuration.

### Tests

159, up from 115. The 44 additions cover every fix above, both rejected findings (so the arguments
do not have to be had twice), and the deep-tail invariants that finite differences cannot check --
the FD harness skips grid points where a greek is smaller than its own error estimate, which left
the extremes unasserted, and that is precisely where overflow and underflow hide. The new
assertions hold by mathematics rather than by tolerance: non-negative gamma and vega, finiteness,
the no-arbitrage price envelope, monotonicity of price in volatility, and delta inside the carry
factor across several hundred contracts.

Put-call parity is unchanged at a worst violation of `2.13e-14` across 72 contracts including
negative rates.

## 1.1.0

Adds the layer between a model and a decision: turning a volatility into a price range, turning a
model into a probability, and turning a set of legs into a payoff. All additive — no existing
signature changed.

The concepts come from [Zerodha Varsity](https://zerodha.com/varsity/module/option-theory/)'s
options modules, which are the material a large number of Indian options traders actually learn
from. Where a Varsity method has a limit the module does not dwell on, both forms are provided and
the limit is measured rather than asserted.

### New: `ExpectedMove` — volatility to an actionable range

A volatility of 12% means nothing at the point of a decision; a 68% range does. This computes it,
along with the square-root-of-time rescaling behind it.

Two constructions, because they disagree where it matters:

- `Range` uses the lognormal form implied by the same process `BlackScholesMerton` prices against,
  including the Ito correction (`-sigma^2/2`) that a surprising amount of code omits.
- `SimpleRange` uses the arithmetic form Varsity teaches, `price * (1 + mean +/- SD)`.

They agree to within 0.5% of spot over a 20-day expiry cycle. They diverge with horizon: at 60%
annualised volatility over a year, two sigma drives the arithmetic lower bound **below zero**,
which is not a price. Tests pin both the agreement and the divergence, so the simple form can be
used where it is valid without anyone mistaking it for the general answer.

One correction to a detail: reproducing Varsity's worked example (NIFTY 8462, daily SD 0.89%, daily
mean 0.04%, 16 days -> upper 8818, lower 8214) requires the drift to be **added to both bounds**,
centring the range on the drifted price. Subtracting it from the lower bound gives 8106, not the
published 8214. The test records this.

### New: `OptionProbability` — what the model says is likely

- `InTheMoneyAtExpiry` — `N(d2)`, the risk-neutral probability of finishing beyond the strike
- `ProbabilityAbove` / `ProbabilityBelow` — the same for any level, which is what you want for a
  BREAKEVEN rather than a strike (a long call expiring one point in the money still loses its
  premium)
- `ProbabilityOfTouching` — the chance of reaching a level at any point before expiry, from the
  reflection principle. Roughly TWICE the chance of finishing beyond it, which is why a stop set
  from an expiry distribution is hit far more often than it looks
- `DeltaVersusProbabilityGap` — how wrong the delta-as-probability rule of thumb is for a given
  contract

That last one is deliberate. Delta-as-probability is taught widely, Varsity included, and it is a
good rule for short-dated near-the-money contracts. It is `N(d1)` against the true `N(d2)`, and the
gap widens with time and volatility — exactly where a premium seller leans on it hardest. This
library returns the size of the error rather than repeating the rule or dismissing it.

All of these are **risk-neutral** probabilities: consistent with traded prices, not the frequencies
you would observe. Using them to argue a short put is a good bet is circular, since they came from
that put's own price. Documented at the class level.

### New: `OptionStrategy` — multi-leg payoffs and position greeks

- `PayoffAtExpiry` — profit or loss at any terminal price
- `Profile` — net cash, maximum profit, maximum loss, and all breakevens
- `NetGreeks` — each leg's greeks scaled by signed quantity and summed

Because a European payoff is piecewise linear with kinks only at strikes, `Profile` is exact rather
than sampled: it evaluates the vertices and solves each segment for its root.

There is deliberately no catalogue of named structures. A vertical spread is two legs; naming it
adds a vocabulary to learn and a taxonomy to argue about, and the names differ between desks.

**Bug found during verification:** the first implementation treated a downside slope as potentially
unbounded, so a short strangle reported `MaxProfit = null`. An underlying can rise without limit but
cannot fall below zero, so only the CALL side can be unbounded — the downside extreme is always the
payoff at a price of zero. Fixed, with a synthetic-long test asserting the position is unbounded
above and bounded below, exactly like the underlying it replicates.

### Tests

80 -> 115. New tests reproduce Varsity's published worked example, pin the divergence between the
two range constructions, verify payoffs for a bull call spread, short strangle, long straddle, iron
condor and synthetic long against hand arithmetic, and assert the piecewise-linearity property that
makes `Profile` exact.

## 1.0.0

Initial release. European option pricing, nine analytic greeks, implied volatility and put-call
parity for four published models, built as one cost-of-carry-parameterised implementation.

### The design

Black-Scholes (1973), Merton (1973), Black (1976) and Garman-Kohlhagen (1983) are one formula at
four values of a single cost-of-carry rate `b`. There is therefore one pricing implementation and
one set of greeks, not four of each, and adding a fifth model later is a new factory method and
nothing else.

`OptionInputs` is an immutable record so a future parameter is a new property rather than a broken
signature. `ImpliedVolatility.Solve` returns a result record rather than a bare double so a better
solver can replace it without touching a caller.

### Included

- Price and nine analytic greeks: delta, gamma, vega, theta, two rhos, vanna, volga, charm
- Implied volatility: Newton inside a maintained bracket, with bisection fallback
- Put-call parity, including deriving either leg from the other
- A double-precision normal distribution: `Cdf`, `Pdf`, `Erf`, `Erfc`, `InverseCdf`

### Two rhos, deliberately

Rho depends on whether the underlying's drift moves with the rate, which is a property of the model
rather than the contract. `RhoCarryTracksRate` is the conventional equity form (use with
Black-Scholes and Merton); `RhoCarryFixed` equals `-T * Price` exactly (use with Black-76). The
input record cannot tell which model produced it, so both are computed and named rather than one
being chosen silently.

### Greeks are raw derivatives until scaled

Vega is returned per one full unit of volatility and theta per year — the unambiguous derivatives.
`GreekConventions` is the only place a display convention is applied, so units are always traceable
to an explicit choice. The default is theta per calendar day, vega per volatility point, rho per
rate point.

### Verification

Three independent checks, all passing:

- **Every greek against a finite-difference bump of `Price`** over 1,440 contracts spanning
  moneyness, maturity, volatility, negative and positive rates and all four models. The tolerance
  accounts for the finite difference's own truncation and round-off error rather than using a flat
  relative threshold, which would have reported failures on points where the difference resolves
  nothing at all.
- **Put-call parity**, model-free: worst gap 2.4e-16 of spot.
- **The Black-Scholes PDE identity** `Theta + b*S*Delta + 0.5*sigma^2*S^2*Gamma == r*V`: worst
  relative residual 1.7e-11.

Plus model equivalences (Black-76 on the forward reproduces Merton on the spot to 8e-14; Merton
with zero dividend is bit-identical to Black-Scholes), implied-volatility round-trips judged
against the problem's own conditioning bound, and degenerate-input limits. 80 tests.

### The normal distribution is not the usual approximation

Abramowitz & Stegun 7.1.26, the five-term polynomial most option-pricing code uses, has a maximum
absolute error near 1.5e-7 and returns **exactly zero** for `Cdf(-10)`, whose true value is
7.62e-24. Deep out-of-the-money prices and finite-difference verification both live in that region.

`Erfc` is instead computed from its convergent definitions — Maclaurin series near the origin
(A&S 7.1.5), continued fraction in the tails (A&S 7.1.14) by modified Lentz — so accuracy is a
property of the mathematics rather than of a coefficient table. Measured over 3,859 points across
[-25, 25]: worst relative error 2.9e-14 for `Cdf`.

The crossover between the two branches was set by measurement, not taste: the continued fraction
needs 829 iterations at x = 0.5 but only 191 at x = 1.0, while the series still reaches 7e-16 at
x = 1.0. An earlier draft crossed over at 0.5 with a 300-iteration cap, silently truncating the
fraction and capping the whole library's accuracy at 7.4e-11 — found by measurement, then fixed.

### Implied volatility reports its own conditioning

`ImpliedVolatilityResult.VegaAtSolution` exposes how much the price moves per unit of volatility at
the answer. Where vega is vanishingly small the solve can be perfect and the volatility meaningless
— measured on a 40-point ITM put a week from expiry, prices at 5% and 40% volatility are identical
to every digit, vega is 8e-71 and `PriceError` is exactly zero. No cutoff is imposed, because where
it belongs depends on the caller's price scale and tick size.

A price outside the no-arbitrage bounds returns `null` rather than the nearest bound: silently
returning a floor volatility for a stale print is how a stale print becomes a trade.

### Scope

European only. Constant volatility — a point on the smile, not the smile. Continuous dividend
yield, not discrete dividends. No data access, no instrument metadata, no transaction costs.
