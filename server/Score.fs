/// Ranking: how likely a release is to be worth hearing. For now, want/have ratio only.
module Digga.Score

/// How many "haves" of evidence the prior is worth. A release with this many haves sits halfway
/// between its own ratio and the prior; one with far more is ranked on its own ratio.
let priorStrength = 20.0

/// Pooled want/have ratio across a set of releases: the prior mean that sparse releases shrink towards.
let priorRatio (stats: (int * int) seq) =
    let want, have = stats |> Seq.fold (fun (w, h) (w', h') -> w + int64 w', h + int64 h') (0L, 0L)
    if have = 0L then 1.0 else float want / float have

/// Want/have ratio with Gamma-Poisson (additive) smoothing — the same idea as IMDb's Bayesian
/// average. Raw want/have ranks a release with 3 wants and 0 haves above one with 2000/400; this
/// pulls low-evidence releases towards the prior instead.
let wantHave (prior: float) (want: int) (have: int) =
    (float want + priorStrength * prior) / (float have + priorStrength)
