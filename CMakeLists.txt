cmake_minimum_required(VERSION 3.25)

project(
    PiRoundtable
    VERSION 0.2.1
    DESCRIPTION "Deterministic meeting state core for Pi Roundtable"
    LANGUAGES CXX
)

option(PI_ROUNDTABLE_BUILD_TESTS "Build Pi Roundtable core tests" ON)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(PI_ROUNDTABLE_BUILD_TESTS)
    include(CTest)
endif()

add_subdirectory(core)
