from __future__ import annotations

from collections.abc import Callable
from pathlib import Path
from typing import Any, cast

import joblib
import matplotlib.pyplot as plt
import numpy as np
import pandas as pd
from sklearn.base import BaseEstimator, clone
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
from sklearn.model_selection import (
	GridSearchCV,
	RandomizedSearchCV,
	StratifiedKFold,
	learning_curve,
	train_test_split,
)
from sklearn.pipeline import Pipeline
from sklearn.preprocessing import PolynomialFeatures, StandardScaler

BASE_DIR = Path(__file__).resolve().parent
CSV_PATH = BASE_DIR / "Data" / "Converted" / "phishing_websites_converted_cleaned.csv"
TARGET_COLUMN = "result"
TEST_SIZE = 0.2
RANDOM_STATE = 43

## Vi bruger én deterministisk split og RandomizedSearchCV med intern parallelisering.


class ElasticNetSafeLogisticRegression(LogisticRegression):
	"""Ensures `l1_ratio` is only active when using elasticnet."""

	def set_params(self, **params):  # type: ignore[override]
		result = super().set_params(**params)
		if getattr(self, "penalty", None) != "elasticnet":
			super().set_params(l1_ratio=None)
		return result

	def fit(self, X, y, sample_weight=None):  # type: ignore[override]
		if getattr(self, "penalty", None) != "elasticnet":
			super().set_params(l1_ratio=None)
		return super().fit(X, y, sample_weight=sample_weight)


def _refine_logistic_model(
	base_pipeline: BaseEstimator,
	best_params: dict[str, Any],
	X_train: pd.DataFrame,
	y_train: pd.Series,
	baseline_score: float,
	inner_cv: StratifiedKFold,
) -> tuple[BaseEstimator, dict[str, Any], float]:
	"""Run a focused grid search around the best random-search parameters."""

	refined_grid: dict[str, Any] = {}
	best_c = float(best_params.get("clf__C", 1.0))
	refined_grid["clf__C"] = np.unique(
		np.clip(best_c * np.array([0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0]), 1e-4, 1e3)
	)
	if best_params.get("clf__penalty") == "elasticnet":
		best_l1 = float(best_params.get("clf__l1_ratio", 0.5))
		steps = np.array([-0.15, -0.1, -0.05, 0.0, 0.05, 0.1, 0.15])
		refined_grid["clf__l1_ratio"] = np.unique(
			np.clip(best_l1 + steps, 0.01, 0.99)
		)

	if all(values.size == 1 for values in refined_grid.values()):
		fitted = clone(base_pipeline).set_params(**best_params)
		return fitted.fit(X_train, y_train), best_params, baseline_score

	grid = GridSearchCV(
		clone(base_pipeline).set_params(**best_params),
		param_grid=refined_grid,
		cv=inner_cv,
		scoring="f1",
		n_jobs=-1,
		refit=True,
		verbose=2,
	)
	grid.fit(X_train, y_train)
	if grid.best_score_ > baseline_score:
		return grid.best_estimator_, grid.best_params_, float(grid.best_score_)

	fallback = clone(base_pipeline).set_params(**best_params)
	return fallback.fit(X_train, y_train), best_params, baseline_score


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
				ElasticNetSafeLogisticRegression(
					solver="saga",
					max_iter=5_000,
					tol=1e-3,
					random_state=random_state,
				),
			),
		]
	)

	param_distributions: dict[str, Any] = {
		"clf__C": np.logspace(-3, 2, 50),
		"clf__penalty": ["l1", "l2", "elasticnet"],
		"clf__l1_ratio": np.linspace(0.05, 0.95, num=19),
		"clf__class_weight": [None, "balanced"],
		"poly__interaction_only": [True, False],
		"poly__degree": [1, 2],
	}

	inner_cv = StratifiedKFold(n_splits=10, shuffle=True, random_state=random_state)
	search = RandomizedSearchCV(
		base_pipeline,
		param_distributions=param_distributions,
		n_iter=2000,
		scoring="f1",
		cv=inner_cv,
		n_jobs=-1,
		random_state=random_state,
		refit=True,
		verbose=2,
	)
	search.fit(X_train, y_train)

	refined_estimator, refined_params, refined_score = _refine_logistic_model(
		cast(Pipeline, search.best_estimator_),
		search.best_params_,
		X_train,
		y_train,
		float(search.best_score_),
		inner_cv,
	)
	best_model = cast(Pipeline, refined_estimator)
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
		"best_params": refined_params,
		"best_score": refined_score,
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
		"n_estimators": np.linspace(200, 800, num=7, dtype=int),
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
		verbose=2,
	)
	search.fit(X_train, y_train)

	best_model = cast(RandomForestClassifier, search.best_estimator_)
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
			(
				"clf",
				ElasticNetSafeLogisticRegression(
					solver="saga",
					max_iter=5_000,
					tol=1e-3,
					random_state=random_state,
				),
			),
		]
	)

	param_distributions: dict[str, Any] = {
		"clf__C": np.logspace(-4, 2, 60),
		"clf__penalty": ["l1", "l2", "elasticnet"],
		"clf__l1_ratio": np.linspace(0.02, 0.98, num=25),
		"clf__class_weight": [None, "balanced"],
		"poly__interaction_only": [True, False],
		"poly__degree": [1, 2],
	}

	inner_cv = StratifiedKFold(n_splits=10, shuffle=True, random_state=random_state)
	search = RandomizedSearchCV(
		base_pipeline,
		param_distributions=param_distributions,
		n_iter=2000,
		scoring="f1",
		cv=inner_cv,
		n_jobs=-1,
		random_state=random_state,
		refit=True,
		verbose=2,
	)
	search.fit(X_train, y_train)

	refined_estimator, refined_params, refined_score = _refine_logistic_model(
		cast(Pipeline, search.best_estimator_),
		search.best_params_,
		X_train,
		y_train,
		float(search.best_score_),
		inner_cv,
	)
	best_model = cast(Pipeline, refined_estimator)
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
		"best_params": refined_params,
		"best_score": refined_score,
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
		"n_estimators": np.linspace(200, 800, num=7, dtype=int),
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
		verbose=2,
	)
	search.fit(X_train, y_train)
	best_model = cast(RandomForestClassifier, search.best_estimator_)
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


def _evaluate_model_from_split(
	model: BaseEstimator,
	X_train: pd.DataFrame,
	X_test: pd.DataFrame,
	y_train: pd.Series,
	y_test: pd.Series,
	target_names: list[str],
) -> dict[str, Any]:
	"""Fit a supplied model on the shared split and gather evaluation metrics."""

	fitted = clone(model).fit(X_train, y_train)
	y_pred = fitted.predict(X_test)
	y_proba: np.ndarray | None
	if hasattr(fitted, "predict_proba"):
		proba_raw = fitted.predict_proba(X_test)
		y_proba = proba_raw[:, 1]
	else:
		y_proba = None
	matrix = confusion_matrix(y_test, y_pred)
	report_text = classification_report(y_test, y_pred, target_names=target_names)
	return {
		"model": fitted,
		"matrix": matrix,
		"y_test": y_test,
		"y_pred": y_pred,
		"y_proba": y_proba,
		"report": report_text,
		"train_accuracy": float(fitted.score(X_train, y_train)),
		"test_accuracy": float(fitted.score(X_test, y_test)),
	}


def _baseline_models_from_split(
	X_train: pd.DataFrame,
	X_test: pd.DataFrame,
	y_train: pd.Series,
	y_test: pd.Series,
	random_state: int,
	target_names: list[str],
) -> dict[str, dict[str, Any]]:
	"""Train untuned baselines to compare against tuned pipelines."""

	logistic_baseline = Pipeline(
		[
			("scaler", StandardScaler()),
			(
				"clf",
				LogisticRegression(
					max_iter=1_000,
					solver="lbfgs",
					random_state=random_state,
				),
			),
		]
	)
	forest_baseline = RandomForestClassifier(
		n_estimators=200,
		random_state=random_state,
		n_jobs=-1,
	)

	return {
		"logistic": _evaluate_model_from_split(
			logistic_baseline, X_train, X_test, y_train, y_test, target_names
		),
		"forest": _evaluate_model_from_split(
			forest_baseline, X_train, X_test, y_train, y_test, target_names
		),
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

	baseline_results = _baseline_models_from_split(
		X_train, X_test, y_train, y_test, RANDOM_STATE, target_names
	)
	baseline_logistic = baseline_results["logistic"]
	baseline_forest = baseline_results["forest"]

	logistic_details = _optimize_logistic_from_split(
		X_train, X_test, y_train, y_test, RANDOM_STATE, target_names
	)
	forest_details = _optimize_forest_from_split(
		X_train, X_test, y_train, y_test, RANDOM_STATE, target_names
	)

	print("\nLogistisk Regression (baseline):")
	print(f"Test accuracy: {baseline_logistic['test_accuracy']:.2%}")
	print(baseline_logistic["report"])

	print("\nLogistisk Regression (tuned):")
	print(f"Test accuracy: {logistic_details['test_accuracy']:.2%}")
	print(logistic_details["report"])
	desc_mapping = globals().get("LOGISTIC_PARAM_DESCRIPTIONS_DA", {})
	for k, v in logistic_details["best_params"].items():
		desc = desc_mapping.get(k, "(ingen beskrivelse)")
		print(f"  {k} = {v} -> {desc}")

	logistic_model: Pipeline = logistic_details["model"]
	baseline_logistic_model = baseline_logistic["model"]

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

	print("Random Forest (baseline):")
	print(f"Test accuracy: {baseline_forest['test_accuracy']:.2%}")
	print(baseline_forest["report"])

	print("\nRandom Forest (tuned):")
	print(f"Test accuracy: {forest_details['test_accuracy']:.2%}")
	print(forest_details["report"])
	for k, v in forest_details["best_params"].items():
		print(f"  {k} = {v}")

	baseline_forest_model = cast(RandomForestClassifier, baseline_forest["model"])
	forest_model: RandomForestClassifier = forest_details["model"]


	forest_importances: pd.Series | None = None
	if hasattr(forest_model, "feature_importances_"):
		forest_importances = pd.Series(
			forest_model.feature_importances_, index=X_df.columns
		).sort_values(ascending=False)
		print("\nRandom Forest feature importances:")
		for feature, score in forest_importances.items():
			print(f"  {feature:<30} {score:.4f}")

	fig_cm, axes_cm = plt.subplots(2, 2, figsize=(14, 10))
	ConfusionMatrixDisplay.from_predictions(
		baseline_logistic["y_test"],
		baseline_logistic["y_pred"],
		display_labels=target_names,
		cmap="Blues",
		ax=axes_cm[0, 0],
	)
	axes_cm[0, 0].set_title("Logistic Regression (baseline)")

	ConfusionMatrixDisplay.from_predictions(
		logistic_details["y_test"],
		logistic_details["y_pred"],
		display_labels=target_names,
		cmap="Blues",
		ax=axes_cm[0, 1],
	)
	axes_cm[0, 1].set_title("Logistic Regression (tuned)")

	ConfusionMatrixDisplay.from_predictions(
		baseline_forest["y_test"],
		baseline_forest["y_pred"],
		display_labels=target_names,
		cmap="Greens",
		ax=axes_cm[1, 0],
	)
	axes_cm[1, 0].set_title("Random Forest (baseline)")

	ConfusionMatrixDisplay.from_predictions(
		forest_details["y_test"],
		forest_details["y_pred"],
		display_labels=target_names,
		cmap="Greens",
		ax=axes_cm[1, 1],
	)
	axes_cm[1, 1].set_title("Random Forest (tuned)")

	fig_cm.suptitle("Confusion Matrices: Baseline vs Tuned")
	fig_cm.tight_layout()

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

	baseline_logistic_scores = baseline_logistic.get("y_proba")
	logistic_scores = logistic_details.get("y_proba")
	baseline_forest_scores = baseline_forest.get("y_proba")
	forest_scores = forest_details.get("y_proba")

	fig_perf, axes_perf = plt.subplots(1, 2, figsize=(14, 5))

	if baseline_logistic_scores is not None:
		log_base_fpr, log_base_tpr, _ = roc_curve(
			baseline_logistic["y_test"], baseline_logistic_scores, pos_label=1
		)
		log_base_auc = auc(log_base_fpr, log_base_tpr)
		axes_perf[0].plot(
			log_base_fpr,
			log_base_tpr,
			label=f"Logistic Baseline (AUC={log_base_auc:.3f})",
			linestyle="--",
		)
		log_base_prec, log_base_rec, _ = precision_recall_curve(
			baseline_logistic["y_test"], baseline_logistic_scores, pos_label=1
		)
		axes_perf[1].plot(
			log_base_rec,
			log_base_prec,
			label="Logistic Baseline",
			linestyle="--",
		)

	if logistic_scores is not None:
		log_fpr, log_tpr, _ = roc_curve(
			logistic_details["y_test"], logistic_scores, pos_label=1
		)
		log_auc = auc(log_fpr, log_tpr)
		axes_perf[0].plot(
			log_fpr,
			log_tpr,
			label=f"Logistic Tuned (AUC={log_auc:.3f})",
		)
		log_prec, log_rec, _ = precision_recall_curve(
			logistic_details["y_test"], logistic_scores, pos_label=1
		)
		axes_perf[1].plot(log_rec, log_prec, label="Logistic Tuned")

	if baseline_forest_scores is not None:
		forest_base_fpr, forest_base_tpr, _ = roc_curve(
			baseline_forest["y_test"], baseline_forest_scores, pos_label=1
		)
		forest_base_auc = auc(forest_base_fpr, forest_base_tpr)
		axes_perf[0].plot(
			forest_base_fpr,
			forest_base_tpr,
			label=f"Forest Baseline (AUC={forest_base_auc:.3f})",
			linestyle="--",
		)
		forest_base_prec, forest_base_rec, _ = precision_recall_curve(
			baseline_forest["y_test"], baseline_forest_scores, pos_label=1
		)
		axes_perf[1].plot(
			forest_base_rec,
			forest_base_prec,
			label="Forest Baseline",
			linestyle="--",
		)

	if forest_scores is not None:
		forest_fpr, forest_tpr, _ = roc_curve(
			forest_details["y_test"], forest_scores, pos_label=1
		)
		forest_auc = auc(forest_fpr, forest_tpr)
		axes_perf[0].plot(
			forest_fpr,
			forest_tpr,
			label=f"Forest Tuned (AUC={forest_auc:.3f})",
		)
		forest_prec, forest_rec, _ = precision_recall_curve(
			forest_details["y_test"], forest_scores, pos_label=1
		)
		axes_perf[1].plot(forest_rec, forest_prec, label="Forest Tuned")

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

	fig_cm.savefig(figures_dir / "confusion_matrices_baseline_vs_tuned.png", dpi=150)

	joblib.dump(baseline_logistic_model, models_dir / "logistic_regression_baseline.joblib")
	joblib.dump(logistic_model, models_dir / "logistic_regression_tuned.joblib")
	joblib.dump(baseline_forest_model, models_dir / "random_forest_baseline.joblib")
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
	fig_perf.savefig(figures_dir / "performance_curves_baseline_vs_tuned.png", dpi=150)

	# Accuracy summary (train vs test for baseline and tuned models)
	fig_acc, ax_acc = plt.subplots(figsize=(10, 5))
	bar_labels = [
		"Logistic Base Train",
		"Logistic Base Test",
		"Logistic Tuned Train",
		"Logistic Tuned Test",
		"Forest Base Train",
		"Forest Base Test",
		"Forest Tuned Train",
		"Forest Tuned Test",
	]
	bar_values = [
		baseline_logistic["train_accuracy"],
		baseline_logistic["test_accuracy"],
		logistic_details["train_accuracy"],
		logistic_details["test_accuracy"],
		baseline_forest["train_accuracy"],
		baseline_forest["test_accuracy"],
		forest_details["train_accuracy"],
		forest_details["test_accuracy"],
	]
	colors = [
		"#aec7e8",
		"#aec7e8",
		"#1f77b4",
		"#1f77b4",
		"#98df8a",
		"#98df8a",
		"#2ca02c",
		"#2ca02c",
	]
	ax_acc.bar(bar_labels, bar_values, color=colors)
	for i, v in enumerate(bar_values):
		ax_acc.text(i, v + 0.005, f"{v:.2%}", ha="center", fontsize=9)
	ax_acc.set_ylim(0, 1.05)
	ax_acc.set_ylabel("Accuracy")
	ax_acc.set_title("Train/Test Accuracy (Baseline vs Tuned)")
	ax_acc.tick_params(axis="x", rotation=20)
	fig_acc.tight_layout()
	fig_acc.savefig(figures_dir / "accuracy_summary_baseline_vs_tuned.png", dpi=150)

	# Learning curves for both models (using training portion only to avoid test leakage)
	fig_lc, axes_lc = plt.subplots(1, 2, figsize=(16, 5))
	train_sizes = np.linspace(0.1, 1.0, num=5)
	cv = StratifiedKFold(n_splits=5, shuffle=True, random_state=RANDOM_STATE)

	# Logistic Regression learning curve
	logistic_estimator = logistic_details["model"]
	(
		log_sizes,
		log_train_scores,
		log_valid_scores,
		_log_fit_times,
		_log_score_times,
	) = learning_curve(
		logistic_estimator,
		X_train,
		y_train,
		cv=cv,
		train_sizes=train_sizes,
		scoring="accuracy",
		n_jobs=-1,
		return_times=True,
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
	(
		forest_sizes,
		forest_train_scores,
		forest_valid_scores,
		_forest_fit_times,
		_forest_score_times,
	) = learning_curve(
		forest_estimator,
		X_train,
		y_train,
		cv=cv,
		train_sizes=train_sizes,
		scoring="accuracy",
		n_jobs=-1,
		return_times=True,
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


