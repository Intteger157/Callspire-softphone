/* SpeexDSP config for Windows MSVC build */
#ifndef CONFIG_H
#define CONFIG_H

#define FLOATING_POINT 1
#define USE_SMALLFT 1

/* SpeexDSP uses EXPORT on function definitions in mdf.c / preprocess.c,
   but the public headers declare them without it. Define EXPORT as empty
   so there is no linkage mismatch. Our own wrapper (webrtc_apm.c) uses
   __declspec(dllexport) directly. */
#define EXPORT

#ifdef _MSC_VER
#define inline __inline
#define restrict
#endif

#endif
