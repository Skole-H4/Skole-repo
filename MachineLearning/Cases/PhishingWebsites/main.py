from __future__ import annotations

import os
from collections.abc import Callable
import os
from collections.abc import Callable
from pathlib import Path
from typing import Any

import joblib
import matplotlib.pyplot as plt
import numpy as np
import pandas as pd
from sklearn.ensemble import RandomForestClassifier
from sklearn.linear_model import LogisticRegression
from sklearn.metrics import (
	ConfusionMatrixDisplay,
	auc,
	classification_report,
	confusion_matrix,
	precision_recall_curve,
	roc_curve,
)
from sklearn.model_selection import RandomizedSearchCV, StratifiedKFold, train_test_split, learning_curve
from sklearn.pipeline import Pipeline
from sklearn.preprocessing import PolynomialFeatures, StandardScaler

BASE_DIR = Path(__file__).resolve().parent
CSV_PATH = BASE_DIR / "Data" / "Converted" / "phishing_websites_converted_cleaned.csv"
TARGET_COLUMN = "result"
TEST_SIZE = 0.2
RANDOM_STATE = 43

## Vi bruger én deterministisk split og RandomizedSearchCV med intern parallelisering.

def _train_and_report(
	X: pd.DataFrame,
	y: pd.Series,
	random_state: int,
	model_factory: Callable[[], Any],
	target_names: list[str],
) -> dict[str, Any]:
	"""Train the provided model factory and compute evaluation artefacts."""

	X_train, X_test, y_train, y_test = train_test_split(
		X,
		y,
		test_size=TEST_SIZE,
		random_state=random_state,
		stratify=y,
	)
	model = model_factory().fit(X_train, y_train)
	y_pred = model.predict(X_test)
	probabilities: np.ndarray | None
	if hasattr(model, "predict_proba"):
		proba_raw = model.predict_proba(X_test)
		probabilities = proba_raw[:, 1]
	else:
		probabilities = None
	matrix = confusion_matrix(y_test, y_pred)
	report_text = classification_report(y_test, y_pred, target_names=target_names)
	return {
		"model": model,
		"matrix": matrix,
		"y_test": y_test,
		"y_pred": y_pred,
		"y_proba": probabilities,
		"report": report_text,
		"train_accuracy": float(model.score(X_train, y_train)),
		"test_accuracy": float(model.score(X_test, y_test)),
	}


def _optimize_logistic(
	X: pd.DataFrame,
	y: pd.Series,
	random_state: int,
	target_names: list[str],
) -> dict[str, Any]:
	"""Perform hyper-parameter tuning and interaction expansion for logistic regression."""
	X_train, X_test, y_train, y_test = train_test_split(
		X,
		y,
		test_size=TEST_SIZE,
		random_state=random_state,
		stratify=y,
	)

	base_pipeline = Pipeline(
		[
			("poly", PolynomialFeatures(degree=2, interaction_only=True, include_bias=False)),
			("scaler", StandardScaler()),
			(
				"clf",
				LogisticRegression(
					solver="saga",
					max_iter=5_000,
					tol=1e-3,
					random_state=random_state,
				),
			),
		]
	)

	param_distributions: dict[str, Any] = {
		"clf__C": np.logspace(-2, 1, 25),
		"clf__penalty": ["l1", "l2"],
		"clf__class_weight": [None, "balanced"],
	}

	inner_cv = StratifiedKFold(n_splits=5, shuffle=True, random_state=random_state)
	search = RandomizedSearchCV(
		base_pipeline,
		param_distributions=param_distributions,
		n_iter=20,
		scoring="f1",
		cv=inner_cv,
		n_jobs=-1,
		random_state=random_state,
		refit=True,
	)
	search.fit(X_train, y_train)

	best_model: Pipeline = search.best_estimator_
	y_pred = best_model.predict(X_test)
	y_proba = best_model.predict_proba(X_test)[:, 1]
	matrix = confusion_matrix(y_test, y_pred)
	report_text = classification_report(y_test, y_pred, target_names=target_names)

	return {
		"model": best_model,
		"matrix": matrix,
		"y_test": y_test,
		"y_pred": y_pred,
		"y_proba": y_proba,
		"report": report_text,
		"train_accuracy": float(best_model.score(X_train, y_train)),
		"test_accuracy": float(best_model.score(X_test, y_test)),
		"best_params": search.best_params_,
	}


def _optimize_forest(
	X: pd.DataFrame,
	y: pd.Series,
	random_state: int,
	target_names: list[str],
) -> dict[str, Any]:
	"""Hyperparameter-tuner Random Forest med RandomizedSearchCV."""

	X_train, X_test, y_train, y_test = train_test_split(
		X,
		y,
		test_size=TEST_SIZE,
		random_state=random_state,
		stratify=y,
	)

	base_forest = RandomForestClassifier(
		random_state=random_state,
		n_jobs=-1,
	)

	param_distributions: dict[str, Any] = {
		"n_estimators": np.linspace(200, 800, 7, dtype=int),
		"max_depth": [None, 5, 10, 20],
		"min_samples_split": [2, 4, 8],
		"min_samples_leaf": [1, 2, 4],
		"max_features": ["sqrt", "log2", None],
	}

	inner_cv = StratifiedKFold(n_splits=5, shuffle=True, random_state=random_state)
	search = RandomizedSearchCV(
		base_forest,
		param_distributions=param_distributions,
		n_iter=25,
		scoring="f1",
		cv=inner_cv,
		n_jobs=-1,
		random_state=random_state,
		refit=True,
	)
	search.fit(X_train, y_train)

	best_model: RandomForestClassifier = search.best_estimator_
	y_pred = best_model.predict(X_test)
	y_proba = best_model.predict_proba(X_test)[:, 1]
	matrix = confusion_matrix(y_test, y_pred)
	report_text = classification_report(y_test, y_pred, target_names=target_names)

	return {
		"model": best_model,
		"matrix": matrix,
		"y_test": y_test,
		"y_pred": y_pred,
		"y_proba": y_proba,
		"report": report_text,
		"train_accuracy": float(best_model.score(X_train, y_train)),
		"test_accuracy": float(best_model.score(X_test, y_test)),
		"best_params": search.best_params_,
	}


def _optimize_logistic_from_split(
	X_train: pd.DataFrame,
	X_test: pd.DataFrame,
	y_train: pd.Series,
	y_test: pd.Series,
	random_state: int,
	target_names: list[str],
) -> dict[str, Any]:
	"""Variant der genbruger et allerede lavet split (ingen intern splitting)."""

	base_pipeline = Pipeline(
		[
			("poly", PolynomialFeatures(degree=2, interaction_only=True, include_bias=False)),
			("scaler", StandardScaler()),
			("clf", LogisticRegression(solver="saga", max_iter=5_000, tol=1e-3, random_state=random_state)),
		]
	)

	param_distributions: dict[str, Any] = {
		"clf__C": np.logspace(-2, 1, 25),
		"clf__penalty": ["l1", "l2"],
		"clf__class_weight": [None, "balanced"],
		"poly__interaction_only": [True],
	}

	inner_cv = StratifiedKFold(n_splits=5, shuffle=True, random_state=random_state)
	search = RandomizedSearchCV(
		base_pipeline,
		param_distributions=param_distributions,
		n_iter=20,
		scoring="f1",
		cv=inner_cv,
		n_jobs=-1,
		random_state=random_state,
		refit=True,
	)
	search.fit(X_train, y_train)

	best_model: Pipeline = search.best_estimator_
	y_pred = best_model.predict(X_test)
	y_proba = best_model.predict_proba(X_test)[:, 1]
	matrix = confusion_matrix(y_test, y_pred)
	report_text = classification_report(y_test, y_pred, target_names=target_names)

	return {
		"model": best_model,
		"matrix": matrix,
		"y_test": y_test,
		"y_pred": y_pred,
		"y_proba": y_proba,
		"report": report_text,
		"train_accuracy": float(best_model.score(X_train, y_train)),
		"test_accuracy": float(best_model.score(X_test, y_test)),
		"best_params": search.best_params_,
	}


def _optimize_forest_from_split(
	X_train: pd.DataFrame,
	X_test: pd.DataFrame,
	y_train: pd.Series,
	y_test: pd.Series,
	random_state: int,
	target_names: list[str],
) -> dict[str, Any]:
	"""Samme som _optimize_forest men bruger fælles split."""

	base_forest = RandomForestClassifier(random_state=random_state, n_jobs=-1)
	param_distributions: dict[str, Any] = {
		"n_estimators": np.linspace(200, 800, 7, dtype=int),
		"max_depth": [None, 5, 10, 20],
		"min_samples_split": [2, 4, 8],
		"min_samples_leaf": [1, 2, 4],
		"max_features": ["sqrt", "log2", None],
	}
	inner_cv = StratifiedKFold(n_splits=5, shuffle=True, random_state=random_state)
	search = RandomizedSearchCV(
		base_forest,
		param_distributions=param_distributions,
		n_iter=25,
		scoring="f1",
		cv=inner_cv,
		n_jobs=-1,
		random_state=random_state,
		refit=True,
	)
	search.fit(X_train, y_train)
	best_model: RandomForestClassifier = search.best_estimator_
	y_pred = best_model.predict(X_test)
	y_proba = best_model.predict_proba(X_test)[:, 1]
	matrix = confusion_matrix(y_test, y_pred)
	report_text = classification_report(y_test, y_pred, target_names=target_names)
	return {
		"model": best_model,
		"matrix": matrix,
		"y_test": y_test,
		"y_pred": y_pred,
		"y_proba": y_proba,
		"report": report_text,
		"train_accuracy": float(best_model.score(X_train, y_train)),
		"test_accuracy": float(best_model.score(X_test, y_test)),
		"best_params": search.best_params_,
	}


def main() -> None:
	"""Direkte tuning af logistisk regression og random forest."""

	if not CSV_PATH.exists():
		raise FileNotFoundError(f"Dataset not found at {CSV_PATH}")

	df = pd.read_csv(CSV_PATH).dropna()
	if TARGET_COLUMN not in df.columns:
		raise KeyError(f"Target column '{TARGET_COLUMN}' not present in dataset")

	y_series = (df[TARGET_COLUMN] == 1).astype(int)
	X_df = df.drop(columns=[TARGET_COLUMN]).astype(float)

	print("Starter direkte hyperparameter-tuning...", flush=True)
	print(f"Antal features (rå): {X_df.shape[1]}")

	target_names = ["Phishing", "Legitimate"]

	# Fælles split til begge modeller
	X_train, X_test, y_train, y_test = train_test_split(
		X_df, y_series, test_size=TEST_SIZE, random_state=RANDOM_STATE, stratify=y_series
	)

	logistic_details = _optimize_logistic_from_split(
		X_train, X_test, y_train, y_test, RANDOM_STATE, target_names
	)
	forest_details = _optimize_forest_from_split(
		X_train, X_test, y_train, y_test, RANDOM_STATE, target_names
	)

	# Gruppér modelleres udskrift med separator
	print("\nLogistisk Regression (tuned):")
	print(f"Test accuracy: {logistic_details['test_accuracy']:.2%}")
	print(logistic_details["report"])
	desc_mapping = globals().get("LOGISTIC_PARAM_DESCRIPTIONS_DA", {})
	for k, v in logistic_details["best_params"].items():
		desc = desc_mapping.get(k, "(ingen beskrivelse)")
		print(f"  {k} = {v} -> {desc}")

	logistic_model: Pipeline = logistic_details["model"]

	logistic_coeffs: pd.Series | None = None
	if isinstance(logistic_model, Pipeline):
		poly = logistic_model.named_steps.get("poly")
		clf = logistic_model.named_steps["clf"]
		if hasattr(clf, "coef_"):
			if isinstance(poly, PolynomialFeatures):
				feature_names = poly.get_feature_names_out(X_df.columns)
			else:
				feature_names = np.asarray(X_df.columns, dtype=str)
			logistic_coeffs = pd.Series(clf.coef_[0], index=feature_names).sort_values(
				key=np.abs,
				ascending=False,
			)
			print("Most impactful features (logistic coefficients):")
			for feature, weight in logistic_coeffs.head(20).items():
				print(f"  {feature:<40} {weight:+.4f}")

	print("\n============================\n")

	print("Random Forest (tuned):")
	print(f"Test accuracy: {forest_details['test_accuracy']:.2%}")
	print(forest_details["report"])
	for k, v in forest_details["best_params"].items():
		print(f"  {k} = {v}")

	forest_model: RandomForestClassifier = forest_details["model"]


	forest_importances: pd.Series | None = None
	if hasattr(forest_model, "feature_importances_"):
		forest_importances = pd.Series(
			forest_model.feature_importances_, index=X_df.columns
		).sort_values(ascending=False)
		print("\nRandom Forest feature importances:")
		for feature, score in forest_importances.items():
			print(f"  {feature:<30} {score:.4f}")

	fig, axes = plt.subplots(1, 2, figsize=(14, 5))
	ConfusionMatrixDisplay.from_predictions(
		logistic_details["y_test"],
		logistic_details["y_pred"],
		display_labels=target_names,
		cmap="Blues",
		ax=axes[0],
	)
	axes[0].set_title("Logistic Regression")

	ConfusionMatrixDisplay.from_predictions(
		forest_details["y_test"],
		forest_details["y_pred"],
		display_labels=target_names,
		cmap="Greens",
		ax=axes[1],
	)
	axes[1].set_title("Random Forest")

	fig.suptitle("Confusion Matrices (Tuned Models)")
	fig.tight_layout()

	fig_feat, axes_feat = plt.subplots(1, 2, figsize=(16, 5))

	if logistic_coeffs is not None:
		# Select top coefficients by absolute magnitude
		top_abs = logistic_coeffs.head(15)
		# Split positives and negatives within this subset
		pos_part = top_abs[top_abs > 0].sort_values(ascending=False)
		neg_part = top_abs[top_abs < 0].sort_values(ascending=True)  # most negative to least negative
		ordered_coeffs = pd.concat([pos_part, neg_part])
		ordered_coeffs.plot.barh(
			ax=axes_feat[0],
			color=["tab:blue" if v > 0 else "tab:orange" for v in ordered_coeffs],
			title="Logistic Regression (coefficients: + then -)",
		)
		# Invert y-axis so first index appears at top
		axes_feat[0].invert_yaxis()
		axes_feat[0].set_xlabel("Coefficient weight")
		axes_feat[0].set_ylabel("Feature")
		axes_feat[0].axvline(0, color="black", linewidth=0.8)
	else:
		axes_feat[0].set_visible(False)

	if forest_importances is not None:
		top_importances = forest_importances.head(15)
		top_importances.iloc[::-1].plot.barh(
			ax=axes_feat[1],
			color="tab:green",
			title="Random Forest (feature importance)",
		)
		axes_feat[1].set_xlabel("Importance")
	else:
		axes_feat[1].set_visible(False)

	fig_feat.suptitle("Top Signals for Phishing Detection")
	fig_feat.tight_layout()

	logistic_scores = logistic_details.get("y_proba")
	forest_scores = forest_details.get("y_proba")

	fig_perf, axes_perf = plt.subplots(1, 2, figsize=(14, 5))

	if logistic_scores is not None:
		log_fpr, log_tpr, _ = roc_curve(
			logistic_details["y_test"], logistic_scores, pos_label=1
		)
		log_auc = auc(log_fpr, log_tpr)
		axes_perf[0].plot(log_fpr, log_tpr, label=f"Logistic (AUC={log_auc:.3f})")
		log_prec, log_rec, _ = precision_recall_curve(
			logistic_details["y_test"], logistic_scores, pos_label=1
		)
		axes_perf[1].plot(log_rec, log_prec, label="Logistic Regression")

	if forest_scores is not None:
		forest_fpr, forest_tpr, _ = roc_curve(
			forest_details["y_test"], forest_scores, pos_label=1
		)
		forest_auc = auc(forest_fpr, forest_tpr)
		axes_perf[0].plot(forest_fpr, forest_tpr, label=f"Random Forest (AUC={forest_auc:.3f})")
		forest_prec, forest_rec, _ = precision_recall_curve(
			forest_details["y_test"], forest_scores, pos_label=1
		)
		axes_perf[1].plot(forest_rec, forest_prec, label="Random Forest")

	axes_perf[0].plot([0, 1], [0, 1], color="tab:gray", linestyle="--", linewidth=1)
	axes_perf[0].set_title("ROC Curve")
	axes_perf[0].set_xlabel("False Positive Rate")
	axes_perf[0].set_ylabel("True Positive Rate")
	axes_perf[0].grid(alpha=0.3)
	axes_perf[0].legend(loc="lower right")

	axes_perf[1].set_title("Precision-Recall Curve")
	axes_perf[1].set_xlabel("Recall")
	axes_perf[1].set_ylabel("Precision")
	axes_perf[1].grid(alpha=0.3)
	axes_perf[1].legend(loc="lower left")

	fig_perf.suptitle("Probability-Based Performance Profiles")
	fig_perf.tight_layout()

	figures_dir = BASE_DIR / "outputs" / "figures"
	models_dir = BASE_DIR / "outputs" / "models"
	figures_dir.mkdir(parents=True, exist_ok=True)
	models_dir.mkdir(parents=True, exist_ok=True)

	fig.savefig(figures_dir / "confusion_matrices.png", dpi=150)

	joblib.dump(logistic_model, models_dir / "logistic_regression_tuned.joblib")
	joblib.dump(forest_model, models_dir / "random_forest_tuned.joblib")
	if logistic_coeffs is not None:
		logistic_coeffs.to_csv(
			figures_dir / "logistic_coefficients.csv", header=["coefficient"]
		)
	if forest_importances is not None:
		forest_importances.to_csv(
			figures_dir / "random_forest_importances.csv", header=["importance"]
		)

	fig_feat.savefig(figures_dir / "feature_signals.png", dpi=150)
	fig_perf.savefig(figures_dir / "performance_curves.png", dpi=150)

	# Accuracy summary (train vs test for both tuned models)
	fig_acc, ax_acc = plt.subplots(figsize=(8, 5))
	bar_labels = ["Logistic Train", "Logistic Test", "Forest Train", "Forest Test"]
	bar_values = [
		logistic_details["train_accuracy"],
		logistic_details["test_accuracy"],
		forest_details["train_accuracy"],
		forest_details["test_accuracy"],
	]
	colors = ["#1f77b4", "#1f77b4", "#2ca02c", "#2ca02c"]
	ax_acc.bar(bar_labels, bar_values, color=colors)
	for i, v in enumerate(bar_values):
		ax_acc.text(i, v + 0.005, f"{v:.2%}", ha="center", fontsize=9)
	ax_acc.set_ylim(0, 1.05)
	ax_acc.set_ylabel("Accuracy")
	ax_acc.set_title("Train/Test Accuracy (Tuned Models)")
	ax_acc.tick_params(axis="x", rotation=20)
	fig_acc.tight_layout()
	fig_acc.savefig(figures_dir / "accuracy_summary.png", dpi=150)

	# Learning curves for both models (using training portion only to avoid test leakage)
	fig_lc, axes_lc = plt.subplots(1, 2, figsize=(16, 5))
	train_sizes = np.linspace(0.1, 1.0, 5)
	cv = StratifiedKFold(n_splits=5, shuffle=True, random_state=RANDOM_STATE)

	# Logistic Regression learning curve
	logistic_estimator = logistic_details["model"]
	log_sizes, log_train_scores, log_valid_scores = learning_curve(
		logistic_estimator,
		X_train,
		y_train,
		cv=cv,
		train_sizes=train_sizes,
		scoring="accuracy",
		n_jobs=-1,
	)
	axes_lc[0].plot(log_sizes, log_train_scores.mean(axis=1), label="Train", marker="o")
	axes_lc[0].plot(log_sizes, log_valid_scores.mean(axis=1), label="Validation", marker="o")
	axes_lc[0].fill_between(
		log_sizes,
		log_train_scores.mean(axis=1) - log_train_scores.std(axis=1),
		log_train_scores.mean(axis=1) + log_train_scores.std(axis=1),
		alpha=0.15,
	)
	axes_lc[0].fill_between(
		log_sizes,
		log_valid_scores.mean(axis=1) - log_valid_scores.std(axis=1),
		log_valid_scores.mean(axis=1) + log_valid_scores.std(axis=1),
		alpha=0.15,
	)
	axes_lc[0].set_title("Logistic Regression Learning Curve")
	axes_lc[0].set_xlabel("Training examples")
	axes_lc[0].set_ylabel("Accuracy")
	axes_lc[0].grid(alpha=0.3)
	axes_lc[0].legend()

	# Random Forest learning curve
	forest_estimator = forest_details["model"]
	forest_sizes, forest_train_scores, forest_valid_scores = learning_curve(
		forest_estimator,
		X_train,
		y_train,
		cv=cv,
		train_sizes=train_sizes,
		scoring="accuracy",
		n_jobs=-1,
	)
	axes_lc[1].plot(forest_sizes, forest_train_scores.mean(axis=1), label="Train", marker="o")
	axes_lc[1].plot(forest_sizes, forest_valid_scores.mean(axis=1), label="Validation", marker="o")
	axes_lc[1].fill_between(
		forest_sizes,
		forest_train_scores.mean(axis=1) - forest_train_scores.std(axis=1),
		forest_train_scores.mean(axis=1) + forest_train_scores.std(axis=1),
		alpha=0.15,
	)
	axes_lc[1].fill_between(
		forest_sizes,
		forest_valid_scores.mean(axis=1) - forest_valid_scores.std(axis=1),
		forest_valid_scores.mean(axis=1) + forest_valid_scores.std(axis=1),
		alpha=0.15,
	)
	axes_lc[1].set_title("Random Forest Learning Curve")
	axes_lc[1].set_xlabel("Training examples")
	axes_lc[1].set_ylabel("Accuracy")
	axes_lc[1].grid(alpha=0.3)
	axes_lc[1].legend()

	fig_lc.suptitle("Learning Curves")
	fig_lc.tight_layout()
	fig_lc.savefig(figures_dir / "learning_curves.png", dpi=150)

	plt.show()


if __name__ == "__main__":
	main()


