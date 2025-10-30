# Start shell på Docker hosten
docker compose -f "d:/Skole/ApacheSpark Docker/docker-compose.yml" exec spark-client bash


# Connect til Spark Masteren med PySpark
docker compose -f "d:/Skole/ApacheSpark Docker/docker-compose.yml" exec spark-client /opt/spark/bin/pyspark --master spark://spark-master:7077



# Exit
exit()



# Læs CSV fil

## Load ratings i dataframe
df = (spark.read.option("header", True).option("inferSchema", True).csv("/opt/spark-data/ml-latest/movies.csv"))
df.printSchema()
df.show(5, truncate=False)

## Load movies i dataframe
df_movies = (spark.read.option("header", True).option("inferSchema", True).csv("/opt/spark-data/ml-latest/movies.csv"))
df.printSchema()
df.show(5, truncate=False)


# Kør app:
>>> import sys
>>> sys.path.append('/opt/spark-apps')   # so Python can find the app/module
>>> import movielens_quickstart as ml # So we can refer to it as "ml."



import sys
sys.path.append('/opt/spark-apps')
import movielens_quickstart as ml
ml.main(spark, "/opt/spark-data")






import importlib, movielens_quickstart as ml
importlib.reload(ml)            # pick up the fix
ml.main(spark, "/opt/spark-data")