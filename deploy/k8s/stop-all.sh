#!/bin/bash

kubectl delete \
    -f ./webspa.yaml \
    -f ./webstatus.yaml \
    -f ./webshoppingagg.yaml \
    -f ./backgroundtasks.yaml \
    -f ./catalog.yaml \
    -f ./ordering.yaml \
    -f ./basket.yaml \
    -f ./payment.yaml \
    -f ./components/pubsub-redis.yaml \
    -f ./components/basket-statestore.yaml \
    -f ./components/sendmail.yaml \
    -f ./apigateway.yaml \
    -f ./identity.yaml \
    -f ./signalr.yaml \
    -f ./seq.yaml \
    -f ./zipkin.yaml \
    -f ./sqldata.yaml \
    -f ./redis.yaml \
    -f ./dapr-config.yaml
