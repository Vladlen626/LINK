"""Team vs Solo in LINK: a Monte Carlo over uncertain parameters, calibrated on the repo's measurements.

Measured (PROJECT_CONTEXT 4-5, 2026-09-30, Codex xhigh, 5 tasks with the harness): a request starts at ~16k tokens
and grows to 55-86k by a task's end, cache ~96 %, 134 calls and 8.0M input for 5 tasks (~27 calls, ~1.6M input each).
That fits input = N*B + h*N^2/2 with B = 16k, h ~ 3k, N ~ 27. Everything else is an assumption with a range.
Unit: "equivalent tokens" as TaskMetrics counts them: fresh input 1, cached 0.1, output 5, times the model's price ratio.
Run: python team_vs_solo.py (Python 3, no packages, a few minutes). docs/TOKENS_PLAN.md has its results of 2026-10-10.
"""
import random, statistics as st

def U(a, b): return random.uniform(a, b)

def session(n, c0, h, o, chi, cold):
    """n requests from context c0 growing by h per request: equivalent tokens (fresh + cached*0.1 + output*5)."""
    inp = n * c0 + h * n * (n - 1) / 2
    return inp * ((1 - chi) + 0.1 * chi) + 5 * o * n + cold

SIZES = {"S": ((12, 20), (2, 2)), "M": ((30, 50), (3, 5)), "L": ((70, 110), (6, 8))}

def trial(size, lead_review=True, fixed=None):
    (n_lo, n_hi), (k_lo, k_hi) = SIZES[size] if size else ((0, 0), (0, 0))
    N, k = fixed if fixed else (round(U(n_lo, n_hi)), random.randint(k_lo, k_hi))
    B, h, o, chi = U(14e3, 20e3), U(2e3, 4e3), U(0.6e3, 2e3), U(0.90, 0.97)
    cold = U(4e3, 12e3) * 0.86                # a new session's first request misses the cache for its own prefix
    p_std, p_light = U(0.3, 0.7), U(0.15, 0.3)   # API price of the Standard (Codex) and Light (Haiku) model vs Opus
    deep = U(1.5, 3.0)                          # output of Deep/xhigh vs the everyday effort
    e_share = U(0.25, 0.40)                     # share of a solo run spent finding its way (search, reads)
    q, rot = U(0.88, 0.96), U(0.0, 0.08)        # solo per-part success after its own check; loss per 100 requests
    pS, pM, pL = U(0.86, 0.95), U(0.70, 0.90), U(0.60, 0.85)
    delta = U(0.0, 0.08)                        # what a fresh session misses of what the lead knew
    catch, check = U(0.5, 0.85), U(0.3, 0.7)    # the lead's review and the project's check find a bad step
    detect = 1 - (1 - catch) * (1 - check)
    r, R = U(2, 6), U(3e3, 6e3)                 # requests a step's worker spends finding its way; its brief
    t_rev, D, cold_rev = U(1, 3), U(2e3, 8e3), U(0.0, 0.5)
    sS, sL = U(0.1, 0.3), U(0.1, 0.3)           # share of Strong and Light steps in a plan

    # Solo: the strong agent at its everyday effort, one session.
    solo = session(N, B + 2e3, h, o, chi, cold)
    p_solo = max(0.0, q - rot * N / 100) ** k
    follow = session(round(0.3 * N), B + h * N, h, o, chi, 0)   # the user's round after a wrong result

    # Team: the lead plans read-only at Deep, then a fresh session per step, the lead reviews the cheaper steps.
    W, P = (1 - e_share) * N, round(e_share * N + 2)
    lead_ctx = B + 3e3 + h * P
    team = session(P, B + 3e3, h, o * deep, chi, cold)
    claude = team
    ok = True
    for _ in range(k):
        x = random.random()
        tier = "S" if x < sS else "L" if x < sS + sL else "M"
        n = round(W / k + r)
        def attempt(t):
            price = {"S": 1.0, "M": p_std, "L": p_light}[t]
            mult = {"S": deep, "M": 1.0, "L": 0.7}[t]
            c = session(n, B + R, h, o * mult, chi, cold) * price
            return c, (c if t != "M" else 0.0)          # Codex spends its own subscription, not Claude's
        def review():
            nonlocal lead_ctx
            fresh = lead_ctx * (1 - 0.1) if random.random() < cold_rev else 0.0
            c = session(round(t_rev), lead_ctx + D, h, o * deep, chi, fresh)
            lead_ctx += D + o
            return c
        p = {"S": pS, "M": pM, "L": pL}[tier] - delta
        c, cl = attempt(tier); team += c; claude += cl
        reviewed = lead_review and tier != "S"
        if reviewed: rc = review(); team += rc; claude += rc
        if random.random() < p: continue
        if random.random() >= (detect if reviewed else check): ok = False; continue   # wrong, and nobody saw it
        c, cl = attempt(tier); team += c / 2; claude += cl / 2                       # redo in the same session
        if reviewed: rc = review(); team += rc; claude += rc
        if random.random() < min(0.98, p + 0.1): continue
        if random.random() >= (detect if reviewed else check): ok = False; continue
        up = {"L": "M", "M": "S", "S": None}[tier]
        if up is None: ok = False; continue                                          # Strong failed: the team stops
        c, cl = attempt(up); team += c; claude += cl
        if reviewed and up != "S": rc = review(); team += rc; claude += rc
        if random.random() >= {"S": pS, "M": pM}[up] - delta: ok = False
    params = dict(B=B, h=h, o=o, chi=chi, p_std=p_std, p_light=p_light, deep=deep, e_share=e_share, r=r, R=R,
                  t_rev=t_rev, D=D, cold_rev=cold_rev, sS=sS, sL=sL)
    return dict(solo=solo, team=team, claude=claude, p_solo=p_solo, ok=ok, follow=follow, params=params)

def run(size, trials=40000, lead_review=True):
    rows = [trial(size, lead_review) for _ in range(trials)]
    ratio = [r["team"] / r["solo"] for r in rows]
    cratio = [r["claude"] / r["solo"] for r in rows]
    p_team = sum(r["ok"] for r in rows) / trials
    p_solo = st.mean(r["p_solo"] for r in rows)
    # cost of a correct result: a wrong one costs the user's round on top (and their time, not counted)
    solo_all = st.mean(r["solo"] + (1 - r["p_solo"]) * r["follow"] for r in rows)
    team_all = st.mean(r["team"] + (0 if r["ok"] else r["follow"]) for r in rows)
    q = lambda xs, f: sorted(xs)[int(f * len(xs))]
    return dict(size=size, solo_k=st.median(r["solo"] for r in rows) / 1e3, ratio_med=st.median(ratio),
                ratio_p10=q(ratio, .1), ratio_p90=q(ratio, .9), team_cheaper=sum(x < 1 for x in ratio) / trials,
                claude_med=st.median(cratio), claude_cheaper=sum(x < 1 for x in cratio) / trials,
                p_solo=p_solo, p_team=p_team, per_ok=team_all / solo_all)

def rank(xs):
    order = sorted(range(len(xs)), key=xs.__getitem__); out = [0] * len(xs)
    for i, j in enumerate(order): out[j] = i
    return out

def spearman(a, b):
    ra, rb = rank(a), rank(b); ma, mb = st.mean(ra), st.mean(rb)
    num = sum((x - ma) * (y - mb) for x, y in zip(ra, rb))
    return num / (sum((x - ma) ** 2 for x in ra) * sum((y - mb) ** 2 for y in rb)) ** 0.5

def sweep():
    print("break-even sweep (lead review on): N requests of a solo run, k steps")
    for N in range(10, 131, 10):
        k = max(2, min(8, round(N / 14)))
        rows = [trial(None, True, (N, k)) for _ in range(8000)]
        ratio = [r["team"] / r["solo"] for r in rows]; cr = [r["claude"] / r["solo"] for r in rows]
        print(f"  N={N:3d} k={k}: all {st.median(ratio):.2f} P<1 {sum(x < 1 for x in ratio) / len(rows):.0%} | "
              f"Claude {st.median(cr):.2f} P<1 {sum(x < 1 for x in cr) / len(rows):.0%} | "
              f"right solo {st.mean(r['p_solo'] for r in rows):.0%} team {sum(r['ok'] for r in rows) / len(rows):.0%}")

def sensitivity(size="M"):
    rows = [trial(size) for _ in range(20000)]
    ratio = [r["team"] / r["solo"] for r in rows]
    names = rows[0]["params"].keys()
    out = sorted(((spearman([r["params"][n] for r in rows], ratio), n) for n in names), key=lambda x: -abs(x[0]))
    print(f"what moves team/solo on {size} (Spearman):", ", ".join(f"{n} {c:+.2f}" for c, n in out[:8]))

if __name__ == "__main__":
    random.seed(7)
    for review in (True, False):
        print("lead review on" if review else "lead review off")
        for size in SIZES:
            m = run(size, lead_review=review)
            print(f"  {m['size']}: solo median {m['solo_k']:.0f}k eq | team/solo all tokens median {m['ratio_med']:.2f} "
                  f"(p10 {m['ratio_p10']:.2f}, p90 {m['ratio_p90']:.2f}), P(team cheaper) {m['team_cheaper']:.0%} | "
                  f"Claude window team/solo {m['claude_med']:.2f}, P(cheaper) {m['claude_cheaper']:.0%} | "
                  f"P(right without the user) solo {m['p_solo']:.0%}, team {m['p_team']:.0%} | "
                  f"cost per right result team/solo {m['per_ok']:.2f}")
    random.seed(11); sweep()
    random.seed(13); sensitivity("M"); sensitivity("L")
