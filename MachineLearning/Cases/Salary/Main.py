# cd d:/Skole/Skole-repo/MachineLearning/Cases/Salary; $env:RANDOM_STATE_LIMIT = '1000000'; ./.venv/Scripts/python.exe Main.py
import os
import time
import threading
from heapq import heappush, heapreplace, nlargest
from multiprocessing import cpu_count
from concurrent.futures import ProcessPoolExecutor, as_completed

import joblib
import matplotlib.pyplot as plt
import numpy as np
import pandas as pd
from sklearn.linear_model import LinearRegression
from sklearn.metrics import mean_squared_error, r2_score
from sklearn.model_selection import train_test_split

CSV_PATH = "Salary_dataset.csv"  # forventer kolonnerne: YearsExperience, Salary
HEAP_LIMIT = 200  # maks. antal kandidater pr. kategori (sweet / underfit / overfit)
UNDERFIT_TRAIN_MAX = 0.6
UNDERFIT_TEST_MAX = 0.2
OVERFIT_TRAIN_MIN = 0.9
OVERFIT_TEST_MAX = 0.6

# Delte variabler i process-pool (initialiseres via _pool_init)
_X_SHARED: np.ndarray | None = None
_Y_SHARED: np.ndarray | None = None


def _pool_init(X_array: np.ndarray, y_array: np.ndarray) -> None:
    """Gem datasæt i hver worker-process for at undgå pickling pr. task."""
    global _X_SHARED, _Y_SHARED
    _X_SHARED = X_array
    _Y_SHARED = y_array


def _evaluate_batch(rs_batch: list[int]) -> list[dict[str, float]]:
    """Evaluer en batch af random_state værdier og returner deres metrics."""
    if _X_SHARED is None or _Y_SHARED is None:
        raise RuntimeError("Pool data ikke initialiseret")

    results: list[dict[str, float]] = []
    X = _X_SHARED
    y = _Y_SHARED

    for rs in rs_batch:
        X_train, X_test, y_train, y_test = train_test_split(
            X, y, test_size=0.2, random_state=rs
        )
        model = LinearRegression().fit(X_train, y_train)
        r2_train = model.score(X_train, y_train)
        r2_test = model.score(X_test, y_test)
        y_train_pred = model.predict(X_train)
        y_test_pred = model.predict(X_test)
        mse_train = mean_squared_error(y_train, y_train_pred)
        mse_test = mean_squared_error(y_test, y_test_pred)
        combined_score = min(r2_train, r2_test)
        diff = abs(r2_train - r2_test)
        results.append(
            {
                "random_state": rs,
                "r2_train": r2_train,
                "r2_test": r2_test,
                "mse_train": mse_train,
                "mse_test": mse_test,
                "combined_score": combined_score,
                "diff": diff,
                "overfit_gap": r2_train - r2_test,
            }
        )

    return results


def _push_limited(
    heap: list[tuple[tuple[float, ...], dict]],
    key: tuple[float, ...],
    candidate: dict,
) -> None:
    """Tilføj kandidat til heap og fasthold en maksimal størrelse."""
    entry = (key, candidate)
    if len(heap) < HEAP_LIMIT:
        heappush(heap, entry)
    else:
        if entry > heap[0]:  # type: ignore[index]
            heapreplace(heap, entry)


def evaluate(
    model: LinearRegression,
    X_tr: pd.DataFrame,
    y_tr: pd.Series,
    X_te: pd.DataFrame,
    y_te: pd.Series,
    name: str = "Model",
) -> None:
    """Udskriv MSE og R^2 for trænings- og testsæt."""
    y_pred_tr = model.predict(X_tr)
    y_pred_te = model.predict(X_te)
    mse_tr = mean_squared_error(y_tr, y_pred_tr)
    mse_te = mean_squared_error(y_te, y_pred_te)
    rmse_tr = mse_tr ** 0.5
    rmse_te = mse_te ** 0.5
    r2_tr = r2_score(y_tr, y_pred_tr)
    r2_te = r2_score(y_te, y_pred_te)
    print(f"\n=== {name} ===")
    print(f"Train -> MSE: {mse_tr:,.2f} | RMSE: {rmse_tr:,.2f} | R^2: {r2_tr:.3f}")
    print(f"Test  -> MSE: {mse_te:,.2f} | RMSE: {rmse_te:,.2f} | R^2: {r2_te:.3f}")


def main() -> None:
    # Indlæs data
    df = pd.read_csv(CSV_PATH)
    print(df.head())

    # Scatter-plot over YearsExperience vs. Salary
    plt.figure()
    plt.scatter(df["YearsExperience"], df["Salary"], alpha=0.85)
    plt.title("Years of Experience vs. Salary")
    plt.xlabel("Years of Experience")
    plt.ylabel("Salary")
    plt.tight_layout()
    # plt.show()

    # Forbered data til model
    X_df = df[["YearsExperience"]]
    y_series = df["Salary"]
    X_array = X_df.to_numpy()
    y_array = y_series.to_numpy()

    # Task distributor for random_state søgning
    max_random_states = 1_000_000
    requested_states = int(os.environ.get("RANDOM_STATE_LIMIT", "5000"))
    total_states = min(max_random_states, requested_states)
    cores = cpu_count() or 1
    workers = max(1, cores - 2)
    print(
        f"Task distributor: evaluerer {total_states} random_state værdier med {workers}"
        f" workers (ud af {cores})"
    )

    chunk_size = 500  # mindre batch-størrelse giver hyppigere progress-opdateringer
    batches = [
        list(range(start, min(start + chunk_size, total_states)))
        for start in range(0, total_states, chunk_size)
    ]

    processed_states_holder = [0]  # mutable container så tråd kan læse/sætte værdien
    start_time = time.time()
    stop_event = threading.Event()
    sweet_heap: list[tuple[tuple[float, ...], dict]] = []
    underfit_heap: list[tuple[tuple[float, ...], dict]] = []
    overfit_heap: list[tuple[tuple[float, ...], dict]] = []
    fallback_underfit_candidate: dict | None = None
    fallback_underfit_score = float("inf")
    fallback_underfit_diff = float("-inf")

    def progress_worker() -> None:
        last_reported = -1
        while not stop_event.is_set():
            time.sleep(1.0)
            processed = processed_states_holder[0]
            if processed == last_reported and processed != total_states:
                continue
            elapsed = time.time() - start_time
            pct = (processed / total_states * 100) if total_states else 100.0
            speed = processed / elapsed if elapsed > 0 else 0.0
            remaining = (
                (total_states - processed) / speed if speed > 0 else float("inf")
            )
            eta_text = f"{remaining:6.1f}s" if remaining != float("inf") else "inf"
            print(
                f"Progress: {processed}/{total_states} ({pct:5.1f}%) "
                f"Elapsed: {elapsed:6.1f}s Speed: {speed:8.1f}/s ETA: {eta_text}",
                flush=True,
            )
            last_reported = processed

    progress_thread = threading.Thread(target=progress_worker, daemon=True)
    progress_thread.start()

    with ProcessPoolExecutor(
        max_workers=workers,
        initializer=_pool_init,
        initargs=(X_array, y_array),
    ) as executor:
        futures = [executor.submit(_evaluate_batch, batch) for batch in batches]
        for future in as_completed(futures):
            batch_results = future.result()
            processed_states_holder[0] += len(batch_results)
            for candidate in batch_results:
                sweet_key = (
                    candidate["combined_score"],
                    -candidate["diff"],
                    candidate["r2_test"],
                    candidate["r2_train"],
                    -candidate["mse_test"],
                    -candidate["mse_train"],
                    -candidate["random_state"],
                )
                underfit_key = (
                    -candidate["combined_score"],
                    candidate["diff"],
                    candidate["mse_test"],
                    candidate["mse_train"],
                    -candidate["r2_test"],
                    -candidate["r2_train"],
                    -candidate["random_state"],
                )
                overfit_gap = candidate["overfit_gap"]
                overfit_key = (
                    overfit_gap,
                    candidate["r2_train"],
                    -candidate["r2_test"],
                    -candidate["mse_test"],
                    -candidate["mse_train"],
                    -candidate["random_state"],
                )
                _push_limited(sweet_heap, sweet_key, candidate)
                if (
                    candidate["r2_train"] <= UNDERFIT_TRAIN_MAX
                    and candidate["r2_test"] <= UNDERFIT_TEST_MAX
                ):
                    _push_limited(underfit_heap, underfit_key, candidate)
                if (
                    candidate["combined_score"] < fallback_underfit_score
                    or (
                        candidate["combined_score"] == fallback_underfit_score
                        and candidate["diff"] > fallback_underfit_diff
                    )
                ):
                    fallback_underfit_candidate = candidate
                    fallback_underfit_score = candidate["combined_score"]
                    fallback_underfit_diff = candidate["diff"]
                if (
                    candidate["r2_train"] >= OVERFIT_TRAIN_MIN
                    and candidate["r2_test"] <= OVERFIT_TEST_MAX
                ):
                    _push_limited(overfit_heap, overfit_key, candidate)

    stop_event.set()
    progress_thread.join(timeout=2.0)
    final_processed = processed_states_holder[0]
    elapsed_total = time.time() - start_time
    final_speed = final_processed / elapsed_total if elapsed_total > 0 else 0.0
    print(
        f"Progress: {final_processed}/{total_states} (100.0%) "
        f"Elapsed: {elapsed_total:6.1f}s Speed: {final_speed:8.1f}/s ETA:   0.0s",
        flush=True,
    )

    if not sweet_heap:
        raise RuntimeError("Ingen random_state kandidater blev evalueret.")

    sweet_entries = nlargest(3, sweet_heap)
    top3 = [entry[1] for entry in sweet_entries]

    print("\n=== Random state-resultater ===")
    print("Top 3 kandidater (mål: høj og balanceret R^2):")
    for rank, candidate in enumerate(top3, start=1):
        print(
            f"  #{rank} rs={candidate['random_state']:7d} | "
            f"R^2 train={candidate['r2_train']:.3f} test={candidate['r2_test']:.3f} | "
            f"MSE train={candidate['mse_train']:.2f} test={candidate['mse_test']:.2f} | "
            f"diff={candidate['diff']:.3f}"
        )

    best_candidate = top3[0]
    best_rs = best_candidate["random_state"]
    print(
        f"\n=== Valgt random_state ===\n"
        f"Sweet spot rs={best_rs} | combined_score={best_candidate['combined_score']:.3f} | "
        f"diff={best_candidate['diff']:.3f}"
    )

    print("\n=== Ekstremtilfælde fra søgningen ===")
    underfit_entries = nlargest(min(1, len(underfit_heap)), underfit_heap)
    underfit_candidate = underfit_entries[0][1] if underfit_entries else None
    used_fallback = False
    if underfit_candidate is None and fallback_underfit_candidate is not None:
        underfit_candidate = fallback_underfit_candidate
        used_fallback = True
    underfit_random_states = {underfit_candidate["random_state"]} if underfit_candidate else set()
    if underfit_candidate is not None:
        prefix = "  Mest underfitting"
        if used_fallback:
            prefix += " (fallback)"
        print(
            f"{prefix}: rs={underfit_candidate['random_state']:7d} | "
            f"R^2 train={underfit_candidate['r2_train']:.3f} test={underfit_candidate['r2_test']:.3f} | "
            f"diff={underfit_candidate['diff']:.3f}"
        )
    else:
        print("  Mest underfitting: ingen kandidater matchede kriterierne")
    overfit_candidate = None
    if overfit_heap:
        for _, candidate in nlargest(len(overfit_heap), overfit_heap):
            if candidate["random_state"] not in underfit_random_states:
                overfit_candidate = candidate
                break
    if overfit_candidate is not None:
        gap_text = overfit_candidate.get("overfit_gap", 0.0)
        print(
            f"  Størst overfitting: rs={overfit_candidate['random_state']:7d} | "
            f"R^2 train={overfit_candidate['r2_train']:.3f} test={overfit_candidate['r2_test']:.3f} | "
            f"train-test diff={gap_text:.3f}"
        )
    else:
        print("  Størst overfitting: ingen kandidater matchede kriterierne")
    print(
        f"  Sweet spot: rs={best_candidate['random_state']:7d} | "
        f"R^2 train={best_candidate['r2_train']:.3f} test={best_candidate['r2_test']:.3f} | "
        f"diff={best_candidate['diff']:.3f}"
    )

    X_train, X_test, y_train, y_test = train_test_split(
        X_df, y_series, test_size=0.2, random_state=best_rs
    )
    model = LinearRegression().fit(X_train, y_train)

    intercept = float(model.intercept_)
    slope = float(model.coef_[0])
    n_samples = len(df)
    x_min_data = float(df["YearsExperience"].min())
    x_max_data = float(df["YearsExperience"].max())

    print("\n=== Sweet spot-model ===")
    print(f"Model: ŷ = {slope:.2f} * YearsExperience + {intercept:.2f}")
    print(f"Hældning: +{slope:.2f} lønenheder pr. ekstra år erfaring")
    print(f"Intercept (0 år): {intercept:.2f} (ekstrapolation hvis få data nær 0)")
    print(f"Data: {n_samples} observationer | erfaring {x_min_data:.1f}–{x_max_data:.1f} år")

    evaluate(model, X_train, y_train, X_test, y_test, "Sweet spot-metrics")

    r2_train = model.score(X_train, y_train)
    p = X_train.shape[1]
    n_train = X_train.shape[0]
    if n_train > p + 1:
        adj_r2_train = 1 - (1 - r2_train) * (n_train - 1) / (n_train - p - 1)
        print(f"Justeret R^2 (train): {adj_r2_train:.3f}")
    else:
        print("Justeret R^2 (train): ikke defineret (for få observationer)")

    print("\n=== Eksempel-forudsigelser ===")
    example_years = [
        round(x_min_data, 1),
        round((x_min_data + x_max_data) / 2, 1),
        round(x_max_data, 1),
    ]
    for yrs in example_years:
        pred_salary = float(model.predict(pd.DataFrame({"YearsExperience": [yrs]}))[0])
        label = "(ekstrapolation)" if yrs < x_min_data or yrs > x_max_data else ""
        print(f"  {yrs:.1f} år erfaring → forventet løn: {pred_salary:,.2f} {label}")

    years_demo = 5.0
    if x_min_data <= years_demo <= x_max_data:
        demo_pred = float(model.predict(pd.DataFrame({"YearsExperience": [years_demo]}))[0])
        print(f"Demo: {years_demo:.1f} år erfaring → {demo_pred:,.2f} i forventet løn")
    else:
        print(f"Demo-år {years_demo} ligger uden for datasættets spænd; springer over.")

    x_min = float(df["YearsExperience"].min())
    x_max = float(df["YearsExperience"].max())
    X_line = pd.DataFrame({"YearsExperience": np.linspace(x_min, x_max, 200)})
    y_line = model.predict(X_line)

    plt.figure()
    plt.scatter(df["YearsExperience"], df["Salary"], alpha=0.85, label="Data")
    plt.plot(X_line["YearsExperience"], y_line, label="Linear Regression")
    plt.title("Linear Regression Fit")
    plt.xlabel("YearsExperience")
    plt.ylabel("Salary")
    plt.legend()
    plt.tight_layout()
    plt.savefig("salary_fit.png", dpi=150)
    plt.close()

    joblib.dump(model, "salary_model.joblib")
    print("Model gemt som salary_model.joblib")


if __name__ == "__main__":
    main()
