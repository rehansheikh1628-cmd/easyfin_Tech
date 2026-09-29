import { Injectable } from '@angular/core';
import { CanActivate, ActivatedRouteSnapshot, RouterStateSnapshot, Router, UrlTree } from '@angular/router';
import { Observable, of } from 'rxjs';
import { map, catchError } from 'rxjs/operators';
import { AuthService } from '../services/auth.service';

@Injectable({
  providedIn: 'root'
})
export class AuthGuard implements CanActivate {
  constructor(private authService: AuthService, private router: Router) {}

  canActivate(
    _route: ActivatedRouteSnapshot,
    state: RouterStateSnapshot
  ): Observable<boolean | UrlTree> | Promise<boolean | UrlTree> | boolean | UrlTree {
    if (this.authService.currentUser) {
      return true;
    }

    return this.authService.checkAuth().pipe(
      map(user => {
        if (user) {
          return true;
        }
        return this.router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
      }),
      catchError(() => {
        return of(this.router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } }));
      })
    );
  }
}
