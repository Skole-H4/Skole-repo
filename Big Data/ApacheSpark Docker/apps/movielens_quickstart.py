# /opt/spark-apps/movielens_steps_json.py
# Solves the specified steps and writes ONE pretty JSON file per result (force overwrite).
# Also writes a simple PNG chart for the "Ekstra opgave".
from pyspark.sql import SparkSession, functions as F, Window
import json, os

# -------------------------
# Helpers (paths & writing)
# -------------------------
def _p(*parts):
    return os.path.join(*[str(p) for p in parts])

def _ensure_dir_for_file(path):
    os.makedirs(os.path.dirname(path), exist_ok=True)

def _overwrite_file(path):
    try:
        os.remove(path)
    except FileNotFoundError:
        pass

def write_json_array_overwrite(df, path):
    """
    Collect small/medium DataFrames to driver and write a single pretty JSON array file.
    Overwrites if the file exists.
    """
    _ensure_dir_for_file(path)
    rows = [r.asDict(recursive=True) for r in df.collect()]
    _overwrite_file(path)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(rows, f, ensure_ascii=False, indent=2)

def write_json_object_overwrite(obj, path):
    _ensure_dir_for_file(path)
    _overwrite_file(path)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(obj, f, ensure_ascii=False, indent=2)

def find_base_dir(DATA):
    """Auto-detect if files live in DATA, DATA/ml-latest, or DATA/ml-25m."""
    candidates = [DATA, _p(DATA, "ml-latest"), _p(DATA, "ml-latest-small"), _p(DATA, "ml-25m")]
    for c in candidates:
        if os.path.exists(_p(c, "ratings.csv")) and os.path.exists(_p(c, "movies.csv")):
            return c
    return DATA

# -------------------------
# Main job
# -------------------------
def main(spark, DATA="/opt/spark-data", target_title=None, sep=","):
    base = find_base_dir(DATA)

    # Load
    ratings = (spark.read.option("header", "true").option("inferSchema", "true")
               .option("sep", sep).csv(_p(base, "ratings.csv")))     # userId,movieId,rating,timestamp
    movies  = (spark.read.option("header", "true").option("inferSchema", "true")
               .option("sep", sep).csv(_p(base, "movies.csv")))      # movieId,title,genres

    out_dir = _p(DATA, "output")
    # -------------------------------------------------------
    # BASAL DATAANALYSE
    # -------------------------------------------------------

    # 1) Hvor mange unikke film er der i datasættet?
    unique_movies_count = movies.select("movieId").distinct().count()
    write_json_object_overwrite(
        {"unique_movies": int(unique_movies_count)},
        _p(out_dir, "unique_movies.json"),
    )

    # 2) Gennemsnitlig rating for hver film samlet
    per_movie_stats = (ratings.groupBy("movieId")
                       .agg(F.avg("rating").alias("avg_rating"),
                            F.count("*").alias("n_ratings"))
                       .join(movies.select("movieId", "title"), "movieId", "left")
                       .select("movieId", "title", F.col("avg_rating"), F.col("n_ratings"))
                      )
    write_json_array_overwrite(per_movie_stats.orderBy("movieId"),
                               _p(out_dir, "avg_rating_per_movie.json"))

    # 3) Top 10 film med højest gennemsnitsrating (ties broken by n_ratings desc)
    top10 = (per_movie_stats
             .orderBy(F.desc("avg_rating"), F.desc("n_ratings"))
             .limit(10))
    write_json_array_overwrite(top10, _p(out_dir, "top10_movies_by_avg.json"))

    # -------------------------------------------------------
    # AVANCERET DATAANALYSE
    # -------------------------------------------------------

    # 4) Konverter timestamp → datetime/year og lav oversigt pr. år (hele datasættet)
    ratings_time = (ratings
        .withColumn("dt", F.to_timestamp(F.from_unixtime("timestamp")))
        .withColumn("year", F.year("dt")))

    yearly_stats = (ratings_time.groupBy("year")
                    .agg(F.avg("rating").alias("avg_rating"),
                         F.count("*").alias("n_ratings"))
                    .orderBy("year"))
    write_json_array_overwrite(yearly_stats, _p(out_dir, "ratings_yearly_stats.json"))

    # 5) Analyser ratings over 10 år for en bestemt film
    #    - If target_title not provided, prefer "Toy Story (1995)" if present,
    #      else pick the most-rated movie.
    if target_title is None:
        if movies.filter(F.col("title") == "Toy Story (1995)").limit(1).count() == 1:
            target_title = "Toy Story (1995)"
        else:
            most_rated = (ratings.groupBy("movieId").agg(F.count("*").alias("n"))
                          .orderBy(F.desc("n")).limit(1)
                          .join(movies, "movieId", "left")
                          .select("title").collect())
            target_title = most_rated[0]["title"] if most_rated else "Unknown"

    target_df = (ratings_time.join(movies, "movieId")
                 .filter(F.col("title") == F.lit(target_title)))

    # define 10-year window from the earliest year available for that film
    years = target_df.select(F.min("year").alias("min_y"), F.max("year").alias("max_y")).collect()[0]
    min_y = int(years["min_y"]) if years["min_y"] is not None else None
    if min_y is None:
        # No ratings found for that title -> write empty result with note
        write_json_array_overwrite(spark.createDataFrame([], "year INT, avg_rating DOUBLE, n_ratings LONG"),
                                   _p(out_dir, "target_movie_ratings_10y.json"))
    else:
        window_end = min_y + 9
        target_10y = (target_df.filter((F.col("year") >= min_y) & (F.col("year") <= window_end))
                      .groupBy("year")
                      .agg(F.avg("rating").alias("avg_rating"),
                           F.count("*").alias("n_ratings"))
                      .orderBy("year"))
        # Add metadata as the first element (year window + title)
        meta = spark.createDataFrame(
            [(target_title, min_y, window_end)],
            "title STRING, start_year INT, end_year INT"
        )
        meta_path = _p(out_dir, "target_movie_ratings_10y_meta.json")
        write_json_array_overwrite(meta, meta_path)  # separate meta file
        write_json_array_overwrite(target_10y, _p(out_dir, "target_movie_ratings_10y.json"))

    # 6) Forbind film med genrer (én række pr. (film, genre))
    movies_genre = (movies
        .withColumn("genre", F.explode(F.split(F.col("genres"), r"\|")))
        .filter(F.col("genre") != "(no genres listed)")
        .select("movieId", "title", "genre"))
    write_json_array_overwrite(movies_genre.orderBy("movieId", "genre"),
                               _p(out_dir, "movie_genre_pairs.json"))

    # 7) Ratings pr. genre (+ identificér den højeste gennemsnitsrating)
    genre_stats = (ratings.join(movies_genre, "movieId")
                   .groupBy("genre")
                   .agg(F.avg("rating").alias("avg_rating"),
                        F.count("*").alias("n_ratings")))
    # Markér den bedste genre
    w = Window.orderBy(F.desc("avg_rating"))
    genre_ranked = (genre_stats
                    .withColumn("rank_by_avg", F.row_number().over(w))
                    .withColumn("is_top", F.col("rank_by_avg") == 1)
                    .orderBy(F.desc("avg_rating")))
    write_json_array_overwrite(
        genre_ranked.select("genre", "avg_rating", "n_ratings", "is_top"),
        _p(out_dir, "genre_stats.json")
    )

    # -------------------------------------------------------
    # Ekstra opgave: Indsæt data i grafik (PNG)
    # -------------------------------------------------------
    # Try to create a small bar chart for top 10 genres by n_ratings.
    # If matplotlib isn't available, skip gracefully.
    try:
        import matplotlib.pyplot as plt
        top_genres = (genre_stats.orderBy(F.desc("n_ratings")).limit(10)
                      .toPandas())
        plt.figure(figsize=(9, 5))
        plt.bar(top_genres["genre"], top_genres["n_ratings"])
        plt.xticks(rotation=45, ha="right")
        plt.title("Top 10 genrer (antal ratings)")
        plt.tight_layout()
        img_path = _p(out_dir, "genre_top10_counts.png")
        _ensure_dir_for_file(img_path)
        _overwrite_file(img_path)
        plt.savefig(img_path, dpi=150)
        plt.close()
    except Exception as e:
        # No matplotlib or headless issues -> ignore
        pass

if __name__ == "__main__":
    spark = SparkSession.builder.appName("MovieLensStepsJSON").getOrCreate()
    try:
        # Change sep=";" if your CSVs are semicolon-separated
        main(spark, DATA="/opt/spark-data", target_title=None, sep=",")
    finally:
        spark.stop()
