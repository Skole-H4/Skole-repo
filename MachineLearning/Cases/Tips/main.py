from __future__ import annotations

from pathlib import Path

import joblib
import matplotlib.pyplot as plt
import numpy as np
import pandas as pd
import seaborn as sns
from sklearn.ensemble import RandomForestClassifier
from sklearn.linear_model import LogisticRegression
from sklearn.metrics import (
	ConfusionMatrixDisplay,
	accuracy_score,
	classification_report,
	confusion_matrix,
)
from sklearn.model_selection import train_test_split

TARGET_COLUMN = "big_tip"
RANDOM_STATE = 42
TEST_SIZE = 0.2


def load_dataset() -> pd.DataFrame:
	"""Load the tips dataset and create the engineered target."""

	raw = sns.load_dataset("tips")
	df = pd.get_dummies(raw, columns=["sex", "smoker", "day", "time"], drop_first=False)
	df[TARGET_COLUMN] = (df["tip"] > 3).astype(int)
	output_path = Path(__file__).with_name("tips_data.xlsx")
	#df.to_excel(output_path, index=False)
	return df


def split_features(df: pd.DataFrame) -> tuple[pd.DataFrame, pd.Series]:
	"""Split dataframe into feature matrix and target vector."""

	feature_columns = [col for col in df.columns if col not in {TARGET_COLUMN, "tip"}]
	X = df[feature_columns].copy()
	y = df[TARGET_COLUMN].copy()
	return X, y


def train_logistic_regression(
	X_train: pd.DataFrame,
	X_test: pd.DataFrame,
	y_train: pd.Series,
	y_test: pd.Series,
	feature_names: list[str],
) -> dict[str, object]:
	"""Fit a logistic regression model and capture evaluation artefacts."""

	estimator = LogisticRegression(
		random_state=RANDOM_STATE,
		solver="liblinear",
		max_iter=1000,
	)
	estimator.fit(X_train, y_train)
	y_pred = estimator.predict(X_test)
	matrix = confusion_matrix(y_test, y_pred)
	accuracy = accuracy_score(y_test, y_pred)
	report_text = classification_report(y_test, y_pred)
	coeffs = estimator.coef_[0]

	return {
		"name": "Logistic Regression",
		"estimator": estimator,
		"matrix": matrix,
		"y_pred": y_pred,
		"y_test": y_test,
		"report": report_text,
		"accuracy": accuracy,
		"coefficients": pd.Series(
			coeffs,
			index=feature_names,
		).sort_values(key=np.abs, ascending=False),
	}


def train_random_forest(
	X_train: pd.DataFrame,
	X_test: pd.DataFrame,
	y_train: pd.Series,
	y_test: pd.Series,
	feature_names: list[str],
) -> dict[str, object]:
	"""Fit a random forest model with all parameters specified."""

	estimator = RandomForestClassifier(
		n_estimators=2000,
		criterion="gini",
		max_depth=None,
		min_samples_split=2,
		min_samples_leaf=1,
		min_weight_fraction_leaf=0.0,
		max_features="sqrt",
		max_leaf_nodes=None,
		min_impurity_decrease=0.0,
		bootstrap=True,
		oob_score=False,
		n_jobs=None,
		random_state=RANDOM_STATE,
		verbose=0,
		warm_start=False,
		class_weight=None,
		ccp_alpha=0.0,
		max_samples=None,
	)
	estimator.fit(X_train, y_train)
	y_pred = estimator.predict(X_test)
	matrix = confusion_matrix(y_test, y_pred)
	accuracy = accuracy_score(y_test, y_pred)
	report_text = classification_report(y_test, y_pred)
	importances = estimator.feature_importances_

	return {
		"name": "Random Forest",
		"estimator": estimator,
		"matrix": matrix,
		"y_pred": y_pred,
		"y_test": y_test,
		"report": report_text,
		"accuracy": accuracy,
		"feature_importances": pd.Series(
			importances,
			index=feature_names,
		).sort_values(ascending=False),
	}


def display_results(results: dict[str, dict[str, object]]) -> plt.Figure:
	"""Plot confusion matrices and feature impact charts for trained models."""

	fig, axes = plt.subplots(2, 2, figsize=(12, 10))

	logistic_result = results["Logistic Regression"]
	rf_result = results["Random Forest"]

	ax_logistic = axes[0, 0]
	ConfusionMatrixDisplay.from_predictions(
		logistic_result["y_test"],
		logistic_result["y_pred"],
		display_labels=["Small Tip", "Big Tip"],
		cmap="Blues",
		ax=ax_logistic,
	)
	ax_logistic.set_title("Logistic Regression Confusion Matrix")

	ax_rf = axes[0, 1]
	ConfusionMatrixDisplay.from_predictions(
		rf_result["y_test"],
		rf_result["y_pred"],
		display_labels=["Small Tip", "Big Tip"],
		cmap="Blues",
		ax=ax_rf,
	)
	ax_rf.set_title("Random Forest Confusion Matrix")

	coeffs = logistic_result.get("coefficients")
	ax_coeff = axes[1, 0]
	if isinstance(coeffs, pd.Series):
		coeffs.iloc[::-1].plot.barh(ax=ax_coeff, color="tab:orange")
		ax_coeff.set_title("Logistic Coefficients (Impact)")
		ax_coeff.set_xlabel("Coefficient")
	else:
		ax_coeff.axis("off")

	importances = rf_result.get("feature_importances")
	ax_imp = axes[1, 1]
	if isinstance(importances, pd.Series):
		importances.iloc[::-1].plot.barh(ax=ax_imp, color="tab:green")
		ax_imp.set_title("Random Forest Feature Importances")
		ax_imp.set_xlabel("Importance")
	else:
		ax_imp.axis("off")

	fig.tight_layout()
	return fig


def main() -> None:
	df = load_dataset()
	X, y = split_features(df)

	X_train, X_test, y_train, y_test = train_test_split(
		X,
		y,
		test_size=TEST_SIZE,
		stratify=y,
		random_state=RANDOM_STATE,
	)

	logistic_result = train_logistic_regression(
		X_train,
		X_test,
		y_train,
		y_test,
		list(X_train.columns),
	)
	forest_result = train_random_forest(
		X_train,
		X_test,
		y_train,
		y_test,
		list(X_train.columns),
	)

	results = {
		"Logistic Regression": logistic_result,
		"Random Forest": forest_result,
	}

	for name, outcome in results.items():
		print(f"{name} accuracy: {outcome['accuracy']:.2%}", flush=True)
		print(outcome["report"], flush=True)

	coeffs = logistic_result.get("coefficients")
	if isinstance(coeffs, pd.Series):
		print("Most impactful features for a big tip (logistic coefficients):")
		for feature, weight in coeffs.items():
			print(f"  {feature:<20} {weight:+.3f}")

	importances = forest_result.get("feature_importances")
	if isinstance(importances, pd.Series):
		print("Random forest feature importances:")
		for feature, score in importances.items():
			print(f"  {feature:<20} {score:.4f}")

	figures_dir = Path("outputs") / "figures"
	models_dir = Path("outputs") / "models"
	figures_dir.mkdir(parents=True, exist_ok=True)
	models_dir.mkdir(parents=True, exist_ok=True)

	fig = display_results(results)
	fig_path = figures_dir / "model_evaluations.png"
	fig.savefig(fig_path, dpi=150)
	plt.show()

	joblib.dump(logistic_result["estimator"], models_dir / "logistic_regression.joblib")
	joblib.dump(forest_result["estimator"], models_dir / "random_forest.joblib")

	if isinstance(coeffs, pd.Series):
		coeffs.to_csv(figures_dir / "logistic_coefficients.csv", header=["coefficient"])
	if isinstance(importances, pd.Series):
		importances.to_csv(figures_dir / "random_forest_importances.csv", header=["importance"])


if __name__ == "__main__":
	main()



