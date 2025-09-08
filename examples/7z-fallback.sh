#!/bin/bash

if [ ! -e /bin/7z ]; then
    ln -s /bin/7zz /bin/7z
fi