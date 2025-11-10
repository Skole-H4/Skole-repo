from __future__ import annotations

import os
import threading
import time
from collections.abc import Callable
from concurrent.futures import ProcessPoolExecutor, as_completed
from heapq import heappush, heapreplace, nlargest
from multiprocessing import cpu_count
from typing import Any

import matplotlib.pyplot as plt
import numpy as np
import pandas as pd
import seaborn as sns
from sklearn.metrics import (
	ConfusionMatrixDisplay,
	classification_report,
	confusion_matrix,
)
from sklearn.model_selection import cross_val_score, train_test_split
from sklearn.neighbors import KNeighborsClassifier
from sklearn.tree import DecisionTreeClassifier


DEFAULT_RANDOM_STATES = 1_000_000
BATCH_SIZE = 500
TOP_K = 3

_X_SHARED: np.ndarray | None = None
_Y_SHARED: np.ndarray | None = None


def _pool_init(X_array: np.ndarray, y_array: np.ndarray) -> None:
	"""Cache dataset copies inside worker processes."""

	global _X_SHARED, _Y_SHARED
	_X_SHARED = X_array
	_Y_SHARED = y_array


def _evaluate_batch(rs_batch: list[int]) -> list[dict[str, Any]]:
	"""Evaluate both models for each random_state in the batch."""

	if _X_SHARED is None or _Y_SHARED is None:
		raise RuntimeError("Worker pool not initialised with dataset")

	X = _X_SHARED
	y = _Y_SHARED
	batch_results: list[dict[str, Any]] = []

	for rs in rs_batch:
		X_train, X_test, y_train, y_test = train_test_split(
			X, y, test_size=0.2, random_state=rs, stratify=y
		)

		tree_model = DecisionTreeClassifier(
			max_depth=5, min_samples_split=2, min_samples_leaf=1, random_state=rs
		).fit(X_train, y_train)
		tree_train_acc = float(tree_model.score(X_train, y_train))
		tree_test_acc = float(tree_model.score(X_test, y_test))

		knn_model = KNeighborsClassifier(n_neighbors=5).fit(X_train, y_train)
		knn_train_acc = float(knn_model.score(X_train, y_train))
		knn_test_acc = float(knn_model.score(X_test, y_test))

		batch_results.append(
			{
				"random_state": rs,
				"tree": {
					"train_acc": tree_train_acc,
					"test_acc": tree_test_acc,
				},
				"knn": {
					"train_acc": knn_train_acc,
					"test_acc": knn_test_acc,
				},
			}
		)

	return batch_results


def _push_top(
	heap: list[tuple[tuple[float, ...], dict[str, Any]]],
	limit: int,
	key: tuple[float, ...],
	candidate: dict[str, Any],
) -> None:
	"""Maintain a bounded heap with the best scoring candidates."""

	entry = (key, candidate)
	if len(heap) < limit:
		heappush(heap, entry)
		return
	if key > heap[0][0]:  # type: ignore[index]
		heapreplace(heap, entry)


def _train_and_report(
	X: pd.DataFrame,
	y: pd.Series,
	random_state: int,
	model_factory: Callable[[], Any],
) -> tuple[float, str, np.ndarray, pd.Series, np.ndarray]:
	"""Train the provided model factory and compute evaluation artifacts."""

	X_train, X_test, y_train, y_test = train_test_split(
		X, y, test_size=0.2, random_state=random_state, stratify=y
	)
	model = model_factory().fit(X_train, y_train)
	y_pred = model.predict(X_test)
	accuracy = float(model.score(X_test, y_test))
	report_text = str(classification_report(y_test, y_pred))
	matrix = confusion_matrix(y_test, y_pred)
	return accuracy, report_text, matrix, y_test, y_pred


def main() -> None:
	"""Train decision tree and k-NN models across many random states."""

	# Load and clean the dataset
	data = sns.load_dataset("penguins").dropna()

	feature_columns = [
		"island",
		"bill_length_mm",
		"bill_depth_mm",
		"flipper_length_mm",
		"body_mass_g",
	]
	X_df = pd.get_dummies(data[feature_columns], columns=["island"], drop_first=True)
	y_series = data["species"].rename("Species")

	pd.concat([X_df, y_series], axis=1).to_csv("penguins_dataset.csv", index=False)

	X_array = X_df.to_numpy(dtype=float)
	y_array = y_series.to_numpy()

	max_random_states = 1_000_000
	requested_states = int(os.environ.get("RANDOM_STATE_LIMIT", DEFAULT_RANDOM_STATES))
	total_states = min(max_random_states, requested_states)
	if total_states < DEFAULT_RANDOM_STATES:
		print(
			"Warning: RANDOM_STATE_LIMIT lowered total iterations to"
			f" {total_states} instead of {DEFAULT_RANDOM_STATES}."
		)

	cores = cpu_count() or 1
	workers = max(1, cores - 1)
	print(
		f"Evaluating {total_states} random_state values using {workers} worker(s)"
		f" on {cores} logical core(s)."
	)

	batches = [
		list(range(start, min(start + BATCH_SIZE, total_states)))
		for start in range(0, total_states, BATCH_SIZE)
	]

	processed_count = [0]
	start_time = time.time()
	stop_event = threading.Event()

	def progress_worker() -> None:
		last_reported = -1
		while not stop_event.is_set():
			time.sleep(1.0)
			processed = processed_count[0]
			if processed == last_reported and processed != total_states:
				continue
			elapsed = time.time() - start_time
			pct = (processed / total_states * 100.0) if total_states else 100.0
			speed = processed / elapsed if elapsed else 0.0
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

	tree_total = 0.0
	knn_total = 0.0
	evaluated_states = 0
	tree_heap: list[tuple[tuple[float, ...], dict[str, Any]]] = []
	knn_heap: list[tuple[tuple[float, ...], dict[str, Any]]] = []

	with ProcessPoolExecutor(
		max_workers=workers,
		initializer=_pool_init,
		initargs=(X_array, y_array),
	) as executor:
		futures = [executor.submit(_evaluate_batch, batch) for batch in batches]
		for future in as_completed(futures):
			batch_results = future.result()
			processed_count[0] += len(batch_results)
			for result in batch_results:
				evaluated_states += 1
				rs = result["random_state"]

				tree_info = {
					"random_state": rs,
					"train_acc": result["tree"]["train_acc"],
					"test_acc": result["tree"]["test_acc"],
				}
				knn_info = {
					"random_state": rs,
					"train_acc": result["knn"]["train_acc"],
					"test_acc": result["knn"]["test_acc"],
				}

				tree_total += tree_info["test_acc"]
				knn_total += knn_info["test_acc"]

				tree_key = (
					tree_info["test_acc"],
					tree_info["train_acc"],
					-float(rs),
				)
				knn_key = (
					knn_info["test_acc"],
					knn_info["train_acc"],
					-float(rs),
				)
				_push_top(tree_heap, TOP_K, tree_key, tree_info)
				_push_top(knn_heap, TOP_K, knn_key, knn_info)

	stop_event.set()
	progress_thread.join(timeout=2.0)
	elapsed_total = time.time() - start_time
	final_speed = processed_count[0] / elapsed_total if elapsed_total else 0.0
	print(
		f"Progress: {processed_count[0]}/{total_states} (100.0%) "
		f"Elapsed: {elapsed_total:6.1f}s Speed: {final_speed:8.1f}/s ETA:   0.0s",
		flush=True,
	)

	if evaluated_states == 0:
		raise RuntimeError("No random_state evaluations completed.")

	tree_mean = tree_total / evaluated_states
	knn_mean = knn_total / evaluated_states
	print(f"Decision tree mean accuracy: {tree_mean:.2%}")
	print(f"KNN mean accuracy: {knn_mean:.2%}")

	tree_top = [entry[1] for entry in nlargest(TOP_K, tree_heap)]
	knn_top = [entry[1] for entry in nlargest(TOP_K, knn_heap)]

	print("\nTop decision tree seeds:")
	for rank, candidate in enumerate(tree_top, start=1):
		print(
			f"  #{rank} random_state={candidate['random_state']:5d} "
			f"train_acc={candidate['train_acc']:.3f} "
			f"test_acc={candidate['test_acc']:.3f}"
		)

	print("\nTop k-NN seeds:")
	for rank, candidate in enumerate(knn_top, start=1):
		print(
			f"  #{rank} random_state={candidate['random_state']:5d} "
			f"train_acc={candidate['train_acc']:.3f} "
			f"test_acc={candidate['test_acc']:.3f}"
		)

	best_tree_seed = tree_top[0]["random_state"]
	best_knn_seed = knn_top[0]["random_state"]

	print(f"\nBest decision tree random_state: {best_tree_seed}")
	tree_accuracy, tree_report, tree_matrix, tree_y_test, tree_y_pred = _train_and_report(
		X_df,
		y_series,
		best_tree_seed,
		lambda: DecisionTreeClassifier(
			max_depth=5, min_samples_split=2, min_samples_leaf=1, random_state=best_tree_seed
		),
	)
	print(f"Decision tree accuracy (test): {tree_accuracy:.2%}")
	print(tree_report)

	print(f"Best k-NN random_state: {best_knn_seed}")
	knn_accuracy, knn_report, knn_matrix, knn_y_test, knn_y_pred = _train_and_report(
		X_df,
		y_series,
		best_knn_seed,
		lambda: KNeighborsClassifier(n_neighbors=5),
	)
	print(f"KNN accuracy (test): {knn_accuracy:.2%}")
	print(knn_report)

	# Cross-validation helps gauge stability across different data folds
	tree_cv_scores = cross_val_score(
		DecisionTreeClassifier(
			max_depth=5,
			min_samples_split=2,
			min_samples_leaf=1,
			random_state=best_tree_seed,
		),
		X_df,
		y_series,
		cv=5,
	)
	print(
		"Decision tree 5-fold CV accuracy: "
		f"mean={tree_cv_scores.mean():.2%}, std={tree_cv_scores.std():.2%}"
	)

	knn_cv_scores = cross_val_score(
		KNeighborsClassifier(n_neighbors=5),
		X_df,
		y_series,
		cv=5,
	)
	print(
		"KNN 5-fold CV accuracy: "
		f"mean={knn_cv_scores.mean():.2%}, std={knn_cv_scores.std():.2%}"
	)

	class_labels = np.unique(y_series)
	fig, axes = plt.subplots(1, 2, figsize=(10, 4))
	ConfusionMatrixDisplay.from_predictions(
		tree_y_test,
		tree_y_pred,
		labels=class_labels,
		ax=axes[0],
	)
	axes[0].set_title("Decision Tree")

	ConfusionMatrixDisplay.from_predictions(
		knn_y_test,
		knn_y_pred,
		labels=class_labels,
		ax=axes[1],
	)
	axes[1].set_title("KNeighborsClassifier")

	fig.suptitle("Confusion Matrices for Best Seeds")
	fig.tight_layout()
	plt.show()


if __name__ == "__main__":
	main()


