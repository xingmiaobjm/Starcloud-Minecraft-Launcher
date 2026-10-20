# libstarcloudjvm.so：JVM 拉起封装（对齐 C# JvmHost.cs 的实证链路）
cmake_minimum_required(VERSION 3.22.1)
project(starcloudjvm C)

add_library(starcloudjvm SHARED starcloud_jvm.c)
find_library(log-lib log)
target_link_libraries(starcloudjvm ${log-lib})
