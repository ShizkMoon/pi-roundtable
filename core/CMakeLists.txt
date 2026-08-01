add_library(pi_roundtable_core STATIC
    src/meeting_state.cpp
)

set_target_properties(pi_roundtable_core PROPERTIES
    POSITION_INDEPENDENT_CODE ON
)

target_include_directories(pi_roundtable_core
    PUBLIC
        ${CMAKE_CURRENT_SOURCE_DIR}/include
)

target_compile_features(pi_roundtable_core PUBLIC cxx_std_20)

if(MSVC)
    target_compile_options(pi_roundtable_core PRIVATE /W4 /permissive-)
else()
    target_compile_options(pi_roundtable_core PRIVATE -Wall -Wextra -Wpedantic)
endif()

add_library(pi_roundtable_c SHARED
    src/c_api.cpp
)

target_link_libraries(pi_roundtable_c PRIVATE pi_roundtable_core)
target_include_directories(pi_roundtable_c PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include)
target_compile_definitions(pi_roundtable_c PRIVATE PR_CORE_EXPORTS)
set_target_properties(pi_roundtable_c PROPERTIES OUTPUT_NAME pi_roundtable_core)

if(PI_ROUNDTABLE_BUILD_TESTS)
    add_executable(pi_roundtable_core_tests
        tests/meeting_state_test.cpp
    )
    target_link_libraries(pi_roundtable_core_tests PRIVATE pi_roundtable_core pi_roundtable_c)
    add_test(NAME pi_roundtable_core_tests COMMAND pi_roundtable_core_tests)
endif()
