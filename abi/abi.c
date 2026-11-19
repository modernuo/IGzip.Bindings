/* Prints the struct sizes, field offsets and constants IGzipBinding.cs hard-codes; CI diffs this against expected.txt. */
#include <stdio.h>
#include <stddef.h>
#include <isa-l/igzip_lib.h>
#define O(T,f) printf(#T "." #f "=%zu ", offsetof(struct T, f))
int main(void) {
  printf("zstream=%zu ", sizeof(struct isal_zstream));
  O(isal_zstream,next_in); O(isal_zstream,avail_in); O(isal_zstream,next_out); O(isal_zstream,avail_out);
  O(isal_zstream,level); O(isal_zstream,level_buf_size); O(isal_zstream,level_buf); O(isal_zstream,end_of_stream);
  O(isal_zstream,flush); O(isal_zstream,gzip_flag); O(isal_zstream,hist_bits); O(isal_zstream,internal_state);
  printf("\ninflate=%zu ", sizeof(struct inflate_state));
  O(inflate_state,next_out); O(inflate_state,avail_out); O(inflate_state,next_in); O(inflate_state,avail_in);
  O(inflate_state,crc_flag); O(inflate_state,hist_bits);
  printf("\nconst LVL1_SMALL=%d LVL2_SMALL=%d LVL3_SMALL=%d IGZIP_ZLIB=%d ISAL_ZLIB=%d\n",
         ISAL_DEF_LVL1_SMALL, ISAL_DEF_LVL2_SMALL, ISAL_DEF_LVL3_SMALL, IGZIP_ZLIB, ISAL_ZLIB);
  return 0;
}
